# FolkIdle on the Oracle Ampere box

**The whole game runs here** — the API and the web client both. Nothing is
left on Render. Only the database stays external, on Supabase.

Why: Render's free instance is 0.15 vCPU / 512 MB and sleeps after 15 minutes,
which for an idle game means the tick loop stops.

## The machine

    ssh folkidle-server        # already in ~/.ssh/config
    92.5.0.94, ubuntu, Ubuntu 24.04, aarch64 (VM.Standard.A1.Flex)
    eu-frankfurt-1, display name "folkidle"

Docker and Compose are installed and usable without sudo. Nothing else is
needed — both halves build inside Docker.

## One origin

Caddy serves the built client from `/srv` and proxies the API to the app
container, on the same hostname. That is worth the small amount of routing
care it costs: no CORS relationship between the two halves, and one
certificate rather than two.

The client and the server collide at exactly one path — the WebSocket connects
to `/`, and `/` is also where `index.html` lives. The `Caddyfile` matches the
upgrade by its **headers** before any path routing runs, so the order of those
three blocks is a contract, not a style choice.

`/gamedata` and `/audio` go to the app, not the file server: the client fetches
its whole content registry from the former. Routing them to static files would
give a client that loads and then knows about no items or monsters at all.

## Audio is NOT a Git LFS asset any more — keep it that way

**This box has no `git lfs`, and that is fine, because nothing it serves needs
it.** It did not used to be fine.

The serving chain is short and entirely inside this repository:

    client/Assets/Resources/Audio/*.wav        11 clips, 4 KB - 132 KB
      -> FolkIdle.Server.csproj links them to  Audio/<name>.wav in the publish output
      -> NetworkBroadcastSystem answers        GET /audio/<name>.wav  (and /audio, a manifest)
      -> the Caddyfile's @api matcher proxies  /audio, /audio/*  to app:8080
      -> client_web/src/lib/ui/audio.ts        fetches, decodes, plays

Every link in that chain was correct from the day it was written, and the game
was still silent in production for its entire life. The clips were tracked in
**Git LFS**; `git pull` on this box, with no git-lfs installed, wrote 130-byte
pointer stubs in their place. The build copied the stubs, the server served
them as `audio/wav`, `decodeAudioData` rejected them, and `audio.ts` recorded a
miss and carried on — a missing clip is survivable by design, so nothing logged
anything. `exercise.mjs` counts absent clips as expected 404s, so it did not
complain either.

**Fixed on 2026-09-02 by taking the runtime audio out of LFS**, not by
installing git-lfs here. The last rule in `.gitattributes` un-sets the LFS
filter for `client/Assets/Resources/Audio/*.wav`, and the eleven clips are now
ordinary git blobs. 367 KB does not need LFS; the ~1 GB that does — the 2048px
source PNGs under `client/Assets/Images/Sprites` — stays in LFS and is never
served (the browser gets the WebP resamples under `SpritesWeb`).

**So: do not put `*.wav` back into LFS, and do not "fix" a silent game by
installing git-lfs here.** Two guards will stop you if you try:

- `ops/validate_audio.py`, run by the `test` job in
  `.github/workflows/deploy.yml`. That checkout is not LFS-enabled either, so
  CI sees exactly what this box would `git pull` — a pointer stub fails the
  pipeline before an image is ever built.
- `server/Dockerfile` re-checks the **publish output**: each `Audio/*.wav` must
  start with `RIFF` and be at least 1 KB. It used to only check that the files
  existed, which a 130-byte stub passes.

Verify after a deploy. A size in the hundreds is the stub coming back:

    # 200 audio/wav 35324 - use GET, not -I. Every method-gated route on this
    # listener answers HEAD with 400, /audio is not special in that.
    curl -s -o /tmp/c.wav -w '%{http_code} %{content_type} %{size_download}\n' \
      https://folkidle.duckdns.org/audio/level_up.wav
    head -c 4 /tmp/c.wav        # RIFF, not "vers" (an LFS pointer starts "version https://")

    curl -s https://folkidle.duckdns.org/audio        # the manifest, 11 files

## Ports: TWO firewalls, and both must be open

The instance's own iptables **and** Oracle's cloud Security List. Opening one
and not the other looks exactly like a dead server.

iptables is done (rules inserted before the existing REJECT and persisted with
`netfilter-persistent save`, so they survive a reboot):

    sudo iptables -I INPUT 4 -p tcp --dport 80  -m state --state NEW -j ACCEPT
    sudo iptables -I INPUT 5 -p tcp --dport 443 -m state --state NEW -j ACCEPT
    sudo netfilter-persistent save

The Security List is **console-only** — there is no OCI CLI or instance
principal on this box, so it cannot be scripted from here:

> Oracle Cloud console → Compute → Instances → `folkidle` → Primary VNIC →
> click the **Subnet** link → Security Lists → the default one →
> **Add Ingress Rules**
>
> | Source Type | Source CIDR | IP Protocol | Source Ports | Destination Port |
> |---|---|---|---|---|
> | CIDR | 0.0.0.0/0 | TCP | *(blank)* | 80 |
> | CIDR | 0.0.0.0/0 | TCP | *(blank)* | 443 |
>
> Leave "Stateless" unchecked. Leave Source Port Range **blank** — a client's
> source port is random, and filling it in silently matches nothing.

Verify from anywhere:

    curl -sv --max-time 10 http://92-5-0-94.sslip.io/ 2>&1 | tail -5

A connection timeout means the Security List is still closed. Port 80 must be
reachable **before** the stack comes up, or Caddy cannot complete the Let's
Encrypt HTTP-01 challenge and there will be no certificate.

## The hostname

`92-5-0-94.sslip.io`. sslip.io answers any A-record-shaped name with the
address spelled into it, so this already resolves to the box with no account,
no registration and no cost.

It is not cosmetic. Let's Encrypt will not issue for a bare IP, the page is
served over https, and an https page cannot open a `ws://` socket.

Swapping in a real domain later means changing the `Caddyfile`'s site address,
the `VITE_FOLKIDLE_SERVER` build arg in `docker-compose.yml`, and
`FOLKIDLE_WEB_ORIGINS` in `.env` — then rebuilding, because **Vite inlines the
server address into the bundle**. A static build has no runtime configuration;
changing the hostname is a rebuild, not a restart.

## Getting the code onto the box

**`git pull` on the box does not work, and has not since it was re-provisioned.**
The repository is private and the box has no GitHub credentials at all - no
credential helper, no `~/.git-credentials`, no deploy key. `git pull` fails with
`could not read Username for 'https://github.com'`, which looks like a network
problem and is not one.

Push to it over SSH instead, from a machine that has the commit. This needs no
GitHub credentials on the box and no token anywhere:

    # once, on the box - lets a push update the checked-out branch
    ssh folkidle-server "cd ~/folkidle && git config receive.denyCurrentBranch updateInstead"

    # then, from the development machine, every deploy
    git push ssh://folkidle-server/home/ubuntu/folkidle main

`updateInstead` refuses if the box's working tree is dirty, which is a feature -
`git stash` there first and the local change is recoverable. It has carried a
local edit to the *root* `docker-compose.yml` (not this one) before now.

The git-lfs warning the push prints is expected and harmless: the box has no
git-lfs, which is exactly why the runtime audio is exempt from it (see **Git
LFS** above).

## Bring it up

    ssh folkidle-server
    cd ~/folkidle/ops/oracle
    cp .env.example .env        # if it does not exist yet
    $EDITOR .env                # DB connection string, JWT_SECRET_KEY, mail
    docker compose up -d --build

The first build takes a while — a .NET publish and an npm install on 2 vCPU.

    docker compose logs -f app        # watch the migration, then "Listening"
    docker compose logs -f caddy      # watch the certificate being obtained
    curl -s https://92-5-0-94.sslip.io/healthz
    curl -sI https://92-5-0-94.sslip.io/     # should be the client, 200 text/html

## Mail, and what happens without it

`FOLKIDLE_RESEND_API_KEY` and `FOLKIDLE_MAIL_FROM` drive the password reset
flow. **Unset means the flow refuses**, deliberately: production falls back to
`DisabledEmailSender`, the request still answers 200 (an unknown address must
stay indistinguishable from a known one) and no mail leaves the box. The
alternative — falling back to the console sender — would print reset links into
the server's own log while telling players to check their inbox, which is worse
than the feature not existing because it looks like it works.

**Resend needs a sending domain you control.** `92-5-0-94.sslip.io` is not one:
sslip.io resolves names, it does not let you add the SPF/DKIM records Resend
verifies against. `onboarding@resend.dev` works with no domain but only delivers
to the address on your own Resend account — fine to prove the pipe, useless for
players.

The sending domain and the site's domain are INDEPENDENT. The reset link points
at whatever `FOLKIDLE_WEB_ORIGINS` says, so you can send from a real domain
while the game still lives on sslip.io.

## Migrations run themselves, and some of them are not additive

The app image migrates on its own entrypoint (`--migrate && exec ...`), so every
pending migration applies the moment the container starts — no prompt, no
separate step. That is safe at exactly one replica and this file says elsewhere
not to scale it.

**Take a Supabase backup before a release that carries a destructive
migration.** The 2026-08-09 release carried two: `BackfillFounderAptitudes`
rewrote live lineage rows, and `RetireTheBank` copied rows out of
`BankEquipmentInstances` and then dropped the table. Both were verified
afterwards against the live database rather than assumed.

## JWT_SECRET_KEY

Copy it from the old Render service rather than generating a new one. Tokens
are signed with it, so a fresh secret silently invalidates every logged-in
player and bounces them to the login screen with no explanation. If it is
rotated deliberately, say so in the release note.

## Cutover, and rollback

Bring this up and verify it **while Render is still serving**, then send
players here. Rollback while the Render services still exist is just telling
people the old address again; once they are suspended it is un-suspending
them. Keep them suspended rather than deleted for a few days.

## Afterwards

Watch for things the free tier was hiding rather than fixing. The tick loop has
real CPU now, so anything that was quietly being starved will start running at
full speed.

Redis holds no persistence by design. That is survivable because the checkpoint
persists gold directly when Redis is absent — it did not always, and a session's
earnings used to be discarded at logout whenever Redis was down.

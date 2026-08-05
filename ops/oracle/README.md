# FolkIdle API on the Oracle Ampere box

What moves: **the API only**. The database stays on Supabase and the web
client stays on Render's static site — that one is free, on a CDN, does not
spin down, and has a working TLS setup there is nothing to gain by moving.

Why: Render's free instance is 0.15 vCPU / 512 MB and sleeps after 15 minutes,
which for an idle game means the tick loop stops. This box is 2 vCPU / 12 GB
and never sleeps.

## The machine

    ssh folkidle-server        # already in ~/.ssh/config
    92.5.0.94, ubuntu, Ubuntu 24.04, aarch64 (VM.Standard.A1.Flex)
    eu-frankfurt-1, display name "folkidle"

Docker and Compose are installed and usable without sudo. Nothing else is
needed — the server builds inside Docker.

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

> Oracle Cloud console → Networking → Virtual Cloud Networks → the VCN in
> eu-frankfurt-1 → Security Lists → Default Security List → Add Ingress Rules
>
> | Source | IP Protocol | Destination Port |
> |---|---|---|
> | 0.0.0.0/0 | TCP | 80 |
> | 0.0.0.0/0 | TCP | 443 |
>
> Leave "Stateless" unchecked.

Verify from anywhere:

    curl -sv --max-time 10 http://92-5-0-94.sslip.io/ 2>&1 | tail -5

A connection timeout means the Security List is still closed. Port 80 must be
reachable **before** the stack comes up, or Caddy cannot complete the Let's
Encrypt HTTP-01 challenge and there will be no certificate.

## The hostname

`92-5-0-94.sslip.io`. sslip.io answers any A-record-shaped name with the
address spelled into it, so this already resolves to the box with no account,
no registration and no cost.

It is not cosmetic. Let's Encrypt will not issue for a bare IP, the client is
served over https, and an https page cannot open a `ws://` socket — so
`92.5.0.94` cannot be the API address. Swapping in a real domain later means
changing one line in the `Caddyfile` and `VITE_FOLKIDLE_SERVER` on the static
site; nothing else refers to it.

## Bring it up

    ssh folkidle-server
    git clone https://github.com/<owner>/IdleHra.git ~/folkidle   # or pull
    cd ~/folkidle/ops/oracle
    cp .env.example .env
    $EDITOR .env            # DB password, and JWT_SECRET_KEY COPIED FROM RENDER
    docker compose up -d --build

The first build takes a while — it is a .NET restore and publish on 2 vCPU.

    docker compose logs -f app        # watch the migration, then "Listening"
    curl -s https://92-5-0-94.sslip.io/healthz

## JWT_SECRET_KEY

Copy it from the Render service. Do not regenerate: tokens are signed with it,
so a new secret silently invalidates every logged-in player and bounces them to
the login screen with no explanation. If it is rotated deliberately, say so in
the release note.

## Cutover

Deploy and verify while Render is still serving, then move traffic.

1. stack up here, `/healthz` answers, a `wss://` upgrade succeeds
2. a real login against the new host (proves it reaches Supabase)
3. set `VITE_FOLKIDLE_SERVER=wss://92-5-0-94.sslip.io` on the Render **static
   site** and redeploy the client
4. `index.html` carries five minutes of CDN cache — verify by fetching the
   bundle and grepping it, not by trusting the deploy status
5. only then suspend the Render **web service** (`srv-d9opsd5bedkc73dfi8h0`)

Rollback is step 3 in reverse: repoint at `https://idlehra.onrender.com` and
redeploy. Keep the Render service suspended rather than deleted for a few days.

## Afterwards

Watch for things the free tier was hiding rather than fixing. The tick loop has
real CPU now, so anything that was quietly being starved will start running at
full speed.

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

## Bring it up

    ssh folkidle-server
    cd ~/folkidle && git pull
    cd ops/oracle
    cp .env.example .env        # if it does not exist yet
    $EDITOR .env                # DB connection string, JWT_SECRET_KEY
    docker compose up -d --build

The first build takes a while — a .NET publish and an npm install on 2 vCPU.

    docker compose logs -f app        # watch the migration, then "Listening"
    docker compose logs -f caddy      # watch the certificate being obtained
    curl -s https://92-5-0-94.sslip.io/healthz
    curl -sI https://92-5-0-94.sslip.io/     # should be the client, 200 text/html

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

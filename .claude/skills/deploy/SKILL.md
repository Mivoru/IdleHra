---
name: deploy
description: Ship FolkIdle to the live Oracle box (server and web client together). Use when asked to deploy, release, push to production, or when checking whether what is live matches main.
---

# Deploying FolkIdle

The whole game runs on one Oracle Ampere box — API and web client both, behind
Caddy on one origin. Only Postgres is external (Supabase). Live at
https://folkidle.duckdns.org / https://92-5-0-94.sslip.io.

**Deploying is a real, outward-facing action affecting live players. Confirm
with the user before doing it, every time.**

## Is what's live already current?

Cheap check, no SSH — a missing endpoint answers 404, a present one answers
401:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://folkidle.duckdns.org/healthz
curl -s -o /dev/null -w "%{http_code}\n" https://folkidle.duckdns.org/api/v1/<endpoint-added-recently>
```

## Before you ship

1. `git status` clean, `main` pushed, and the suite green — see the `verify`
   skill. There is no staging environment.
2. **Does this release carry a destructive migration?** Migrations apply
   automatically on container start (`--migrate && exec ...`) — no prompt, no
   separate step. If any migration drops, rewrites or backfills existing rows,
   **take a Supabase backup first** and say so. Precedent: one release
   rewrote live lineage rows and another dropped a table after copying it out.
3. **Did the hostname change?** Vite inlines the server address into the
   bundle. Changing it means editing the `Caddyfile` site address, the
   `VITE_FOLKIDLE_SERVER` build arg in `ops/oracle/docker-compose.yml` and
   `FOLKIDLE_WEB_ORIGINS` in `.env` — then rebuilding. It is a rebuild, not a
   restart.
4. **Never rotate `JWT_SECRET_KEY` casually.** Tokens are signed with it; a new
   secret silently logs out every player and bounces them to the login screen
   with no explanation.

## Ship

**`git pull` on the box does not work** — the repo is private and the box has no
GitHub credentials (no helper, no deploy key). It fails with `could not read
Username for 'https://github.com'`, which reads like a dead network and is not.
Push to the box over SSH instead:

```bash
# once, on the box
ssh folkidle-server "cd ~/folkidle && git config receive.denyCurrentBranch updateInstead"

# every deploy, from the machine that has the commit
git push --no-verify ssh://folkidle-server/home/ubuntu/folkidle main
ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose up -d --build"
```

`updateInstead` refuses a dirty working tree on the box — `git stash` there
first, and say so, because it has held a local edit to the root
`docker-compose.yml` before. The git-lfs warning on push is expected.

The stack itself:

```bash
cd ~/folkidle/ops/oracle
docker compose up -d --build
```

The first build is slow — a .NET publish plus an npm install on 2 vCPU.

## Watch it come up

```bash
docker compose logs -f app      # the migration, then "Listening"
docker compose logs -f caddy    # the certificate, if the hostname changed
curl -s  https://folkidle.duckdns.org/healthz
curl -sI https://folkidle.duckdns.org/       # 200 text/html = the client
```

Then run the browser check against production **read-only**:

```powershell
cd client_web
$env:FOLKIDLE_E2E_BASE='https://folkidle.duckdns.org/'; npm run smoke:screens
```

Use `smoke:screens`, not `exercise` — `exercise` spends items, marries
villagers and rerolls affixes on whatever account it signs into. `smoke:screens`
signs in as a guest and only navigates.

`FOLKIDLE_E2E_BASE` genuinely aims it as of 2026-09-02; before that the script
hardcoded localhost and ignored the variable, so this post-deploy step had been
smoke-testing the developer's own dev server and reporting a pass for a box it
had never opened.

## Things that look like a dead server but are not

- **Two firewalls.** The box's iptables *and* Oracle's cloud Security List.
  Opening one and not the other is indistinguishable from a dead host. The
  Security List is console-only; there is no OCI CLI on the box.
- **Port 80 must be reachable before the stack comes up**, or Caddy cannot
  complete the Let's Encrypt HTTP-01 challenge and there is no certificate.
- **Do not scale to more than one replica.** Migrations run on every container
  start, and `SimulationEngine._activePlayers` is per-process live state.
- **Mail is off unless configured.** Without `FOLKIDLE_RESEND_API_KEY` and
  `FOLKIDLE_MAIL_FROM`, password reset falls back to `DisabledEmailSender`: the
  request still answers 200 (an unknown address must stay indistinguishable
  from a known one) and no mail is sent.

Full detail, including the Security List table and the reasoning: `ops/oracle/README.md`.

# Task 34: retire the Unity project, revalidated 2026-09-23

This file only records what has changed. **The step-by-step source is still
`docs/superpowers/plans/2026-09-17-retire-unity-project.md`.** Apply the
corrections below to it as you go. `docs/TASK_BOARD.md` §34 (line ~3535, not
~3570) approves the task and asks for three things: revalidate the plan, "move
whatever the server actually serves before removing anything", and deploy and
smoke-test production after the last step.

Everything here was checked read-only against `main` @ `219d2bf`. Nothing was
built, run or deleted.

---

## Revalidation results

### Headline

1. **The plan's reader list still holds. Nothing new reads `client/` since
   2026-09-17.** `git log --since=2026-09-17 -- client/` is empty. The last
   commit to touch `client/` is `dfde217` (2026-09-13). None of the 115 commits
   since then changed a reader. The only commit in that window that touched
   `.github/` is `d0c548a`, the Android SDK `tools` fix, which is unrelated.
2. **Task 21's server split moved nothing that references `client/`.** Both
   `.csproj` files are still at `server/FolkIdle.Server/` and
   `server/FolkIdle.Server.Tests/`, so their `../../client/...` relative paths
   still resolve. The only `.cs` files that mention `client/`, `StreamingAssets`
   or `Resources/Audio` do so in comments (`NetworkBroadcastSystem.cs:741,758,6249`,
   `ContentRegistry.cs:1532`, `HardenedEngineIntegrationTests.cs:7211`). None of
   them opens a path.
3. **The deletion frees much less than "~1 GB".** See the size table below. The
   Unity-only tracked content is about **44 MB in 1,422 files**. The ~1 GB is the
   source art, which the plan keeps. The 4.2 GB you see locally is
   `client/Library`, which is gitignored, exists only on this machine, and is
   not something `git rm` touches.
4. **Two steps in the old plan would fail on this machine as written.** Task 3
   Step 5's `find client -type f` would list ~52,000 ignored local files, and
   `FolkIdle.Client.asmdef` is missing from the delete list. Details and fixes
   are under Corrections.

### Claim by claim

| Old-plan claim | Status | Evidence (2026-09-23) |
|---|---|---|
| `generate-sprites.mjs` reads `client/Assets/Images/SpritesWeb` | **Still true** | `client_web/scripts/generate-sprites.mjs:32` `spriteRoot = join(repoRoot,'client','Assets','Images','SpritesWeb')`. The content source is `server/GameData` (line 45), **not** `StreamingAssets`, so deleting `client/Assets/StreamingAssets/GameData` is safe. |
| `ops/tools/generate_sprites.py` reads the art | **Still true** | Lines 26-27: reads `WithWhiteBackground`, writes `Sprites`. This is a dev tool, and neither CI nor the build runs it. |
| `web.Dockerfile` COPYs SpritesWeb | **Still true** | `ops/oracle/web.Dockerfile:20` `COPY client/Assets/Images/SpritesWeb /client/Assets/Images/SpritesWeb`. The `>100 files` guard is still at the end of the build stage. |
| `server/Dockerfile` is "comments only" | **Changed: it is load-bearing** | Its code does not name `client/`, but the **Oracle compose builds the server from it** (`ops/oracle/docker-compose.yml:26,33`: `context: ../..`, `dockerfile: server/Dockerfile`). Its `COPY . .` of the repo root is what lets the csproj globs find the audio and SpritesWeb. Its audio check (RIFF header, ≥1 KB, per clip) is the production proof that audio survived. Treat it as a reader. |
| `validate_audio.py` reads the clips | **Still true** | `ops/validate_audio.py:95` defaults `--path` to `client/Assets/Resources/Audio`. CI runs it (`deploy.yml:45`). |
| `audio.ts` reads `client/Assets` | **Changed: comment only** | `client_web/src/lib/ui/audio.ts:12` is a comment. The code fetches `/audio/*` over HTTP from the server. It is not a path reader. |
| `FolkIdle.Server.csproj` links two globs | **Still true** | Line 38 `../../client/Assets/Resources/Audio/*.wav` → `Audio/`. Line 59 `../../client/Assets/Images/SpritesWeb/**/*.webp` → `Sprites/`. |
| `FolkIdle.Server.Tests.csproj` Compile-Includes `TutorialStateMachine.cs` | **Still true** | Line 40, with a backslash path. That is why a `client/Assets` grep misses it (see Corrections). The file is 124 lines and uses only `System`. It is used by `HardenedEngineIntegrationTests.cs:5580`. It is not in the production image: `server/Dockerfile` publishes only `FolkIdle.Server`. |
| `vite.config.ts` copies the sprites | **Still true** | `client_web/vite.config.ts:28` `copySprites()`. If the directory is missing it **warns and continues**. The only thing that turns a missing directory into a failure is the web.Dockerfile guard. |
| `unity_client.yml` is live, gated on `UNITY_LICENSE` | **Still true** | It triggers on `client/**` pushes to `main` and does nothing without the secret. Deleting it in the same commit as the tree is safe: Actions reads workflows from the pushed commit, so the deletion push does not run it. |
| `.gitattributes` must not change | **Still true** | Line 60 `client/Assets/Resources/Audio/*.wav !filter binary`. Under leave-in-place it needs no edit. |
| `tools/clean_sprites.py`, `tools/prepare_backgrounds.py` are dev-only readers | **Still true** | `clean_sprites.py:39-40` (Sprites → SpritesWeb). `prepare_backgrounds.py:17` (relative path `client/Assets/Images/WithWhiteBackground/Background`). |
| "`client/Library/` does not exist in this checkout" | **Wrong on this machine** | It exists and is 4.2 GB. Also ignored and untracked: `client/Logs`, `UserSettings`, `obj`, `bin`, `.claude/`, `.mcp.json`, `Assets/_Recovery/`, `Assets/_Recovery.meta`. None of these is in git, so none reaches CI or the box. They do break any `find client ...` check (see Corrections). |
| 959 `.meta` files | **Still true** | `git ls-files client \| grep -c '\.meta$'` = 959. There are 16,903 on disk, most of them under the ignored `Library`. |
| Scripts: "164 of 165 files" Unity-only | **Roughly true** | Tracked non-meta files: Editor 6, Engine 39, Network 17, UI 104, plus **`Scripts/FolkIdle.Client.asmdef`**, which the plan does not list. |
| Audio: 11 `.wav` files, 371 KB | **True, plus a README** | 11 clips plus a tracked `client/Assets/Resources/Audio/README.md` and its `.meta`. Keep the README. It is doc, not Unity. |
| SpritesWeb 18 MB | **True** | 215 tracked files, all `.webp`, not in LFS. |
| NEXT_STEPS_BACKLOG entry "Not started" | **Partly done already** | `NEXT_STEPS_BACKLOG.md:559-575` already points at the old plan and names the sixth reader, but it still says "**Not started**". Old Task 1 Step 4 is half done. Only the status line, the dev-only tools and the `server/Dockerfile` note need adding. |

### New finding: `client_web/scripts/generate-sprites.mjs` header text

The generated-file header (line ~490) says the server links the art "out of
`client/Assets/Images/Sprites`". The csproj actually links `SpritesWeb`. The
header is stale but harmless, and `--check` byte-compares the generated file.
**Do not fix it in this task**, because a header edit changes
`sprites.generated.ts` and turns a deletion commit into a regenerate commit.
Leave it for task 35's leftovers.

### Sizes (measured)

| What | Size |
|---|---|
| `client/` on this disk | 5.7 GB. 4.2 GB of it is ignored `Library`, and 1.5 GB is `Assets` with LFS smudged. |
| Tracked blobs under `client/` in HEAD | 532.7 MB in 2,064 files. LFS files count as ~130-byte pointers. |
| **Kept** by the plan: Images, Audio, `TutorialStateMachine.cs` (no `.meta`) | 642 files, 488 MB. Includes 328 MB of `WithWhiteBackground` and 150 MB of `Sprites` stored as full pre-LFS blobs. |
| **Deleted** by the plan | **1,422 files, 44.3 MB.** Plugins/NuGet 17 MB, TextMesh Pro 9 MB, Scripts 1.5 MB, plus 959 `.meta` files. |
| Repo `.git` pack | 485 MB. **It does not shrink.** History keeps every deleted blob. |

(Measured with `git ls-tree -r -l HEAD client`. One file has a non-ASCII name,
`WithWhiteBackground/Background/JAK_by_mělo_vypadat_main.png`. `git` quotes it
unless you pass `-c core.quotepath=off`. It is a KEEP.)

### Production box: does the deletion matter there?

- **Transfer.** Deploys are `git push --no-verify ssh://folkidle-server/home/ubuntu/folkidle main`
  followed by `docker compose up -d --build` (`.claude/skills/deploy/SKILL.md`,
  `ops/oracle/README.md:165-191`). The deletion commit is tree objects only, so
  the push is tiny. `receive.denyCurrentBranch updateInstead` then removes the
  1,422 files from the box's working tree. The push **refuses** if that tree is
  dirty, so `git stash` there first and say so, as the skill says.
- **Disk.** It saves about 44 MB of working tree on the box. `.git` does not
  shrink. Freeing ~1 GB is **not** a reason to do this task, and it does not
  happen.
- **Build context.** There is **no `.dockerignore` anywhere** (none at the root
  and no `*.Dockerfile.dockerignore`).
  - `server/Dockerfile` does `COPY . .` of the repo root. Today the box's server
    image build context includes all of tracked `client/`: the Unity project plus
    ~480 MB of full-blob source PNGs. After the deletion it is 44 MB smaller. The
    PNGs stay in the context, and excluding them with a `.dockerignore` is
    out of scope (see "Not in this plan").
  - `web.Dockerfile` copies only `client_web/` and `SpritesWeb`, so BuildKit
    sends only those paths. The deletion does not change this image.
- **What production actually serves, and from where** (`ops/oracle/caddy/Caddyfile`):
  - `/sprites/*` comes from **Caddy's static bundle**, meaning the web image's
    `dist/sprites`, copied out of SpritesWeb. The app's own `Sprites/` link
    (`NetworkBroadcastSystem.cs:775`) is not on the public path.
  - `/audio`, `/audio/*` go to the app (`@api`, line 143) and come from
    `Audio/` in the publish output, linked out of `Resources/Audio`.
  - `/gamedata` goes to the app and comes from `server/GameData`. It does not
    depend on `client/` at all, but check it anyway as a control.
- **CI does not build either production image.** `build-and-push` builds
  `server/FolkIdle.Server/Dockerfile` with `context: server`, a different
  Dockerfile. The only real production-image proof is the build **on the box**.
  CI does cover `check:sprites` (`deploy.yml:134`), `validate_audio.py`
  (`:45`), both csproj builds, the full test suite (`:60,:67,:120`) and
  `vite build` (`:177`). Its checkout is deliberately non-LFS, which makes it
  the same as the box.

### The task board's "move whatever the server serves" vs the plan's "leave in place"

These agree in outcome. The server keeps serving from the same paths because
nothing moves. **Recommendation: keep leave-in-place.** A move changes seven
readers plus `.gitattributes` in the same commit as the deletion, for a
cosmetic gain. If the owner does want a move, it becomes its own later PR after
this one has been live, and that PR has to update all of these together:

- both csproj files
- `web.Dockerfile:20`
- `vite.config.ts:28`
- `generate-sprites.mjs:32`
- `validate_audio.py:95`
- `.gitattributes:60`
- the two `tools/*.py` and `ops/tools/generate_sprites.py`

Put this choice to the owner at the go-ahead gate. Do not decide it silently.

---

## Corrections to the 2026-09-17 plan

**Task 1, Step 1.**
- Replace `grep -n "client/Assets" ... client_web/src/lib/ui/audio.ts` with a
  note that `audio.ts` is comment-only.
- Add `server/Dockerfile` and `ops/oracle/docker-compose.yml` as a reader pair.
- Use a backslash-tolerant pattern so the Tests csproj is found by the same grep:
  ```
  git grep -nE "client[/\\\\]Assets" -- server ops client_web/scripts client_web/vite.config.ts tools .github .gitattributes
  ```

**Task 1, Step 2.** Replace the `grep -rln ... .` with `git grep`. The
original walks the 4.2 GB ignored `client/Library` and `node_modules`:
```
git grep -lE "client[/\\\\](Assets|Library|ProjectSettings|Packages)" -- . ':!client/'
```
The expected hit list is unchanged from the old plan (verified 2026-09-23: 29
files, all accounted for).

**Task 1, Step 4.** The backlog entry already names the plan and the sixth
reader. Only these need adding:
- change "**Not started.**" to in progress, pointing at both plan files
- add `server/Dockerfile` (via compose) as a reader
- add the two `tools/*.py`
- add the "44 MB, not 1 GB" size fact

**Task 2, Step 1 (manifest).**
- DELETE also: `client/Assets/Scripts/FolkIdle.Client.asmdef`.
- KEEP also: `client/Assets/Resources/Audio/README.md`.
- Replace the `client/Library` note with: "ignored local-only dirs (`Library`,
  `Logs`, `UserSettings`, `obj`, `bin`, `.claude`, `.mcp.json`,
  `Assets/_Recovery*`) are not tracked; clean them locally only if the owner
  wants the disk back, as a separate non-git step".

**Task 2, Step 3 (`python3 ops/validate_audio.py`).** This machine's Python
cannot import `encodings` (TASK_BOARD §35). The step fails locally for reasons
unrelated to the clips. Accept **CI's `test` job** result instead. Or, if a
working interpreter exists (`py -3`), use it and say which one ran.

**Task 2, Step 5 and Task 3, Step 6 (local `docker build`).** Do **not** run
them from this working tree. With no `.dockerignore`:
- `web.Dockerfile`'s `COPY client_web/ ./` would copy the local Windows
  `client_web/node_modules` (win32 esbuild binaries) and `android/` over the
  Linux `npm ci`.
- A server build via `server/Dockerfile` would ship the 4.2 GB `Library` as
  context.

Run both production images from a **clean worktree** instead:
```
git worktree add ../idlehra-unity-check HEAD
git -C ../idlehra-unity-check lfs pull
cd ../idlehra-unity-check
docker build -f ops/oracle/web.Dockerfile -t folkidle-web-unity-check .
docker build -f server/Dockerfile       -t folkidle-app-unity-check .
```
- The `lfs pull` is not strictly needed; the box has no LFS either. Skipping
  it is actually the more faithful test.
- Also build `server/Dockerfile`. It is the image production runs, and its
  RIFF check is the audio proof.
- Afterwards remove both images and `git worktree remove ../idlehra-unity-check`.
- For Task 3, check out the deletion branch in that worktree.

**Task 3, Step 2.** Add
`git rm "client/Assets/Scripts/FolkIdle.Client.asmdef"`. Scripts also carries
two orphan metas whose directories no longer exist (`Diagnostics.meta`,
`Input.meta`), and `git rm -r Scripts/Editor` does not remove the sibling
`Editor.meta`. Step 3 removes all of them. Prefer `git rm` to `find -delete`, so that nothing
untracked is touched:
```
git ls-files -z "client/Assets/Scripts/Engine" | grep -zv "/TutorialStateMachine.cs$" | xargs -0 git rm -q
```

**Task 3, Step 3.** Replace `find client -name "*.meta" -delete` with
`git ls-files -z "client/*.meta" | xargs -0 git rm -q`. The `find` would also
delete metas inside the ignored `Library/` and `_Recovery/`. That is harmless
but out of scope.

**Task 3, Step 5.** Replace `find client -type f | sort` with
`git -c core.quotepath=off ls-files client | sort`. Expected: exactly
`client/Assets/Images/{Sprites,SpritesWeb,WithWhiteBackground}/**` (630 files),
`client/Assets/Resources/Audio/*.wav` (11) plus `README.md`, and
`client/Assets/Scripts/Engine/TutorialStateMachine.cs`, for **643 files**. Fail
on any other line.

**Task 3, Step 8.** `git add -A client` is unnecessary after `git rm`, and it
would stage nothing extra because the local junk is ignored. Commit with
`git commit` alone and check `git show --stat HEAD | tail -1` reports about
1,423 files deleted (1,422 plus `unity_client.yml`).

**Paths.** Wherever the old plan writes `server/FolkIdle.Server.Tests.csproj`,
the real path is `server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`.

---

## How to approach this (for the implementing agent)

**Order.** Old Task 1, then Task 2, then an **owner gate**, then Task 3, then
deploy and verify, then old Task 3 Step 9. Tasks 1 and 3 each get their own
branch/PR and commit. Task 2 produces no diff.

1. **Task 1 (docs PR).** Run the corrected greps. Update the backlog entry. Open
   a PR.

2. **Task 2 (baseline, before any deletion).** Record each result:
   - From `client_web/`: `npm run check:sprites`.
   - With the server stopped (the hook blocks otherwise): `dotnet build` of both
     csproj files.
   - The two `docker build`s from a clean worktree (above).
   - CI's `test` job for `validate_audio.py`.
   - **A production baseline, taken now**, using the same curls as step 6
     below. You compare against it after the deploy.

3. **GATE: explicit owner go-ahead before Task 3 starts.** Show the owner:
   - the Task 2 results
   - the manifest (643 files kept, ~1,423 deleted)
   - the fact that this frees ~44 MB, not ~1 GB
   - the leave-in-place vs move question

   Do not delete anything without a "yes" in the owner's own message. An
   agent's message does not count. The deploy then needs its own confirmation,
   per the deploy skill.

4. **Task 3 on a branch.** Apply the corrected steps. **Before the deletion
   commit is pushed or opened as a PR, all of these must pass on the post-delete
   tree:**
   - `git ls-files client` matches the 643-file manifest exactly
   - `npm run check:sprites`
   - `dotnet build` of `FolkIdle.Server.csproj` and `FolkIdle.Server.Tests.csproj`
   - `dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj`
     (Docker up). It must include `Test_TutorialStateMachine_BlocksNonTutorialUiUntilStepsComplete`.
   - `docker build` of **both** `ops/oracle/web.Dockerfile` and
     `server/Dockerfile` from a clean worktree of the branch. The web guard
     `>100 sprites` and the server's "Audio: 11 real clips" line must both
     print.
   - `git grep -nE "client[/\\\\]Assets" -- ':!docs' ':!IdleHraGDD' ':!client'`.
     Every hit must point at a path that still exists.

   Then open the PR. CI must be green on `test` and `client`, which re-run
   `validate_audio.py`, `check:sprites`, the csproj builds and the suite on a
   non-LFS checkout.

5. **Merge, then deploy (with the deploy confirmation).**
   ```bash
   ssh folkidle-server "cd ~/folkidle && git status --short"   # must be clean, or stash and say so
   # rollback insurance: tag the currently running images first (names from `docker compose images`)
   ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose images"
   ssh folkidle-server "docker tag <app-image>:latest <app-image>:pre-unity-retire && docker tag <web-image>:latest <web-image>:pre-unity-retire"
   ssh folkidle-server "git -C ~/folkidle rev-parse HEAD"         # note the pre-deploy commit
   git push --no-verify ssh://folkidle-server/home/ubuntu/folkidle main
   ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose up -d --build"
   ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose logs --tail=200 app | grep -i -e 'Audio:' -e Listening"
   ```
   The build log must show `Audio: 11 real clips in the publish output.`, and
   the web build must not print `BUILD FAILED: dist/sprites`.

6. **Production verification.** Compare against the Task 2 baseline:
   ```bash
   B=https://folkidle.duckdns.org
   curl -s -o /dev/null -w "healthz %{http_code}\n" $B/healthz
   curl -sI $B/ | head -1                                                        # 200, text/html
   curl -s $B/gamedata | head -c 300; echo                                       # manifest, as before
   curl -s -o /dev/null -w "gamedata/items %{http_code} %{size_download}\n" $B/gamedata/items.json
   curl -s $B/audio; echo                                                        # manifest, 11 clips
   curl -s -o /tmp/c.wav -w "audio %{http_code} %{content_type} %{size_download}\n" $B/audio/level_up.wav   # GET, not -I; 200 audio/wav 35324
   head -c 4 /tmp/c.wav; echo                                                    # RIFF
   curl -s -o /tmp/s.webp -w "sprite %{http_code} %{content_type} %{size_download}\n" $B/sprites/Backgrounds/abyssal_breach.webp   # 200 image/webp, same size as baseline
   head -c 12 /tmp/s.webp | tail -c 4; echo                                      # WEBP
   ssh folkidle-server "cd ~/folkidle/ops/oracle && docker compose exec -T app sh -c 'ls Audio/*.wav | wc -l; find Sprites -type f | wc -l'"   # 11 and 215
   ```
   (`/audio` rejects HEAD with 400 on this listener, so use GET, per
   `ops/oracle/README.md:76`. Run the same set against
   `https://92-5-0-94.sslip.io` if you want to cover both hostnames.)

   Then the read-only browser check. Use **only** `smoke:screens` against
   production, never `exercise`:
   ```powershell
   cd client_web
   $env:FOLKIDLE_E2E_BASE='https://folkidle.duckdns.org/'; npm run smoke:screens
   ```
   Load one item-heavy screen (the Chest) on a phone or in a browser and confirm
   the icons are artwork, not two-letter placeholders. Then close the backlog
   item (old Task 3 Step 9).

## Rollback

The deletion changes no reader, so a rollback should only ever be needed for
something this revalidation missed.

- **Fast, no rebuild.** Retag the images tagged in step 5 back to `:latest` and
  run `docker compose up -d --no-build` on the box. This restores the previous
  serving behaviour in seconds, and the working tree does not matter to running
  containers.
- **Proper.** Run `git revert <deletion-sha>` locally, which restores all 1,423
  files from history (the blobs never left `.git`). Push to `origin` via a PR,
  then `git push --no-verify ssh://folkidle-server/home/ubuntu/folkidle main`
  and `docker compose up -d --build`. Re-run step 6. The revert also restores
  `unity_client.yml`, which does nothing without `UNITY_LICENSE`.
- **Partial.** If only one reader broke (for example, a file that should have
  been kept), restore it alone rather than reverting the whole deletion:
  ```
  git checkout <deletion-sha>^ -- <path>
  ```
  Commit it and redeploy.
- **Never** roll back by force-pushing the box to an older commit. Its
  `updateInstead` checkout is also the build context, and a forced push past
  commits already live there loses track of what is deployed. Use a revert or
  the image retag.

## Not in this plan (added)

- A root `.dockerignore`. It would stop `server/Dockerfile`'s `COPY . .` from
  shipping ~480 MB of source PNGs, `client_web/node_modules` and so on to the
  daemon. It is worth doing as its own PR. It must **not** exclude
  `client/Assets/Images/SpritesWeb` or `client/Assets/Resources/Audio`, and it
  needs the same two-image build proof.
- Deleting the ignored local `client/Library` and friends (4.2 GB on this
  machine). This is local-only and needs no commit. Ask the owner.
- The stale `generate-sprites.mjs` header line (see "New finding").

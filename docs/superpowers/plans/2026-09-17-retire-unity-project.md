# Retire the Unity project

> **Revalidated 2026-09-23. Read `docs/superpowers/plans/2026-09-23-task-34-retire-unity-revalidated.md` first. It corrects several steps below (a missing `.asmdef`, `find` versus `git ls-files`, local docker builds, the owner gate, deploy and rollback).**

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the retired Unity project (`client/ProjectSettings`, `client/Packages`, the Unity-only files inside `client/Assets`, and `.github/workflows/unity_client.yml`) without breaking any of the live pipelines that read raw source art/audio and one shared C# file out of `client/Assets` — the sprite generator, the production Docker build, the audio validator, and both server `.csproj` files.

**Architecture:** `client/Assets` is not one thing. By file count it is mostly a Unity project's internals (959 `.meta` files, `ProjectSettings/`, `Packages/`, scenes, prefabs, `TextMesh Pro`, an Addressables catalogue, 164 of 165 files under `Scripts/`). By size it is almost entirely raw source art that a live pipeline still reads: `Images/Sprites` (648 MB, the 2048px masters), `Images/WithWhiteBackground` (809 MB, `tools/prepare_backgrounds.py`'s input), and `Images/SpritesWeb` (18 MB, what the server and the production Docker build actually ship) together account for 1.5 GB of `client/Assets`' ~1.5 GB total. `Resources/Audio` (11 `.wav` files, 371 KB) is the only other survivor. One more file survives for a different reason: `server/FolkIdle.Server.Tests.csproj` compiles `client/Assets/Scripts/Engine/TutorialStateMachine.cs` directly (`<Compile Include>`, not an asset copy) — it is genuine shared game logic with zero `UnityEngine` references, not Unity-specific cruft, and this plan's own research (Task 1) found it because it was missing from the backlog's list of five known readers.

**Tech stack:** C# / .NET 8 (two `.csproj` files), Node/Vite (`client_web`), Python 3 (`ops/tools/generate_sprites.py`, `ops/validate_audio.py`), Docker (`ops/oracle/web.Dockerfile`), GitHub Actions.

**Spec:** `docs/architecture/NEXT_STEPS_BACKLOG.md`, "# OPEN BACKLOG ITEM, added 2026-09-17 - retire the Unity project" (lines ~73-109 as of this writing). That entry's list of five readers is the starting point for this plan, not the final word — Task 1 re-verifies it and adds a sixth reader it missed.

## Global Constraints

- **This is inherently risky: it touches a live production Docker build and a live CI workflow.** Verify every reader's path locally — `npm run generate:sprites` (really `check:sprites`, the `--check` variant, so it fails loudly instead of silently rewriting output), `python3 ops/validate_audio.py`, `dotnet build` on both `.csproj` files, and a full `docker build -f ops/oracle/web.Dockerfile` from the repo root — **before** Task 3 (the deletion) runs, not after.
- **Task 3 (the deletion) must not be merged or deployed without an explicit human go-ahead**, separate from and later than the go-ahead for Tasks 1-2. Tasks 1-2 only add documentation and run verification commands; nothing they do is destructive or requires the same level of caution. Say so again at the top of Task 3 below, not just here.
- **Each task is independently revertible and gets its own commit.** Do not combine "confirm the reader list," "verify the pipelines locally," and "delete the Unity project" into one commit — a revert of the deletion must not also revert the research notes, and a revert of the research notes must never accidentally touch the deletion.
- **The recommendation this plan implements is LEAVE `client/Assets` IN PLACE — do not rename or move it.** See "Move vs. leave in place" below. This means no reader's path changes at all in this plan; Task 2 is verification, not a path migration.
- **Do not touch `.gitattributes`.** Its `client/Assets/Resources/Audio/*.wav !filter binary` exception is path-scoped and depends on the audio files staying exactly where they are; leaving `client/Assets` in place means this file needs zero changes. Do not "fix" it as a drive-by.
- **Do not attempt to fix the pre-existing LFS hygiene issue found during this plan's research** (some files under a `filter=lfs` glob, e.g. `client/Assets/Images/Sprites/Characters/Bes_Female.png`, are committed as full blobs rather than LFS pointers — they predate the LFS migration and `.gitattributes`' own comment already documents that history is not rewritten). It is unrelated to this plan, and `git rm` on files being deleted for other reasons does not touch it either way — leave it alone.

### Move vs. leave in place

**Recommendation: leave `client/Assets` exactly where it is.** A rename (e.g. to a new top-level `assets/`) would need every one of six readers' paths updated in the same change as the production Dockerfile, both `.csproj` files, `vite.config.ts`'s sprite-copy plugin, the sprite/audio generator scripts, and `.gitattributes`' path-scoped LFS exception — all for a purely cosmetic win, on a change that already touches a live Docker build and CI. Leaving it in place means Task 2 is pure verification with no path edits at all, and Task 3's deletion is the only risky step, which is exactly the risk profile Global Constraints asks for. `client/` also stops looking like a client at all once Task 3 lands — it will contain only `Assets/` (itself almost entirely `Images/` and `Resources/Audio`, plus the one surviving script) — so the "confusing name" objection is weaker after this plan than before it. A rename remains a reasonable follow-up; it is out of scope here (see "Not in this plan").

---

### Task 1: Confirm every reader, and that none were missed

**Files:**
- Modify: `docs/architecture/NEXT_STEPS_BACKLOG.md` (the "OPEN BACKLOG ITEM, added 2026-09-17 - retire the Unity project" section — update it in place rather than appending a second entry, since this plan supersedes its "not started" status)

**Interfaces:** none — this task changes documentation only.

- [ ] **Step 1: Re-verify the five already-known readers against the live repo**

  Run each of these and confirm the match still exists at (approximately) the stated location:

  ```
  grep -n "client/Assets" client_web/scripts/generate-sprites.mjs ops/tools/generate_sprites.py
  grep -n "client/" ops/oracle/web.Dockerfile
  grep -n "client/Assets" ops/validate_audio.py client_web/src/lib/ui/audio.ts
  grep -n "client/Assets" server/FolkIdle.Server/FolkIdle.Server.csproj server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  cat .github/workflows/unity_client.yml
  ```

  Expected (confirmed during this plan's own research, 2026-09-17): all five still hold. `FolkIdle.Server.csproj` links two globs (`../../client/Assets/Resources/Audio/*.wav`, `../../client/Assets/Images/SpritesWeb/**/*.webp`); `FolkIdle.Server.Tests.csproj` was NOT matched by a plain `client/Assets` grep against `client/Assets` glob patterns because its own reference is a single `Compile Include` of an exact file path — see Step 3 below, this is the sixth reader.

- [ ] **Step 2: Repo-wide grep for anything else touching `client/`**

  ```
  grep -rln "client/Assets\|client/Library\|client/ProjectSettings\|client/Packages\|client/Scripts\|projectPath: client\b" --include="*" . | grep -v "^\./client/" | grep -v "/\.git/"
  ```

  Expected hits beyond the five confirmed readers, and their disposition:
  - `server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj` — **the sixth reader**, see Step 3.
  - `server/Dockerfile`, `client_web/vite.config.ts`, `tools/clean_sprites.py`, `tools/prepare_backgrounds.py`, `client_web/tests/tutorial.test.ts`, `client_web/src/lib/ui/sprites.generated.ts`, `client_web/src/lib/ui/sprites.missing.txt`, `client_web/MOBILE.md`, `ops/oracle/README.md` — all comments or generated-file headers describing the same five-plus-one readers already accounted for; no independent path dependency of their own. (`tools/clean_sprites.py` and `tools/prepare_backgrounds.py` are dev-only utility scripts, not part of any automated build, but they DO read/write real paths under `client/Assets/Images` — note them in the backlog update in Step 4 even though they aren't part of `npm run build` or CI, since a future run of either would break silently if the tree moved. They do not affect the leave-in-place decision.)
  - `docs/architecture/CURRENT_IMPLEMENTATION_STATE.md`, `docs/architecture/GAME_DESIGN_SPEC.md`, `docs/architecture/WEB_CLIENT_PORT_PLAN.md`, `docs/AUDIO_REQUIREMENTS.md`, `docs/TASK_BOARD.md` — prose referencing Unity source files, some plausibly stale (e.g. `CodexRegionsCache.cs`/`UiCodexRegionsWindow.cs` mentions). Documentation only; no action required by this plan, but do not treat these as evidence of a live reader without checking each one individually if a future task touches them.
  - `IdleHraGDD/chatHistory*.txt`, `IdleHraGDD/codexHistory.txt` — historical chat logs, not code. Ignore.

  If this grep turns up anything else not listed above, stop and investigate before proceeding to Task 2 — this plan's deletion list in Task 3 is only as safe as this grep being complete.

- [ ] **Step 3: Confirm the sixth reader in detail**

  ```
  grep -n "Compile Include.*client" server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  ```

  Expected: `<Compile Include="..\..\client\Assets\Scripts\Engine\TutorialStateMachine.cs" Link="Client\TutorialStateMachine.cs" />`. This is the ONLY `Compile Include` anywhere under `server/` that reaches into `client/` (confirmed by `grep -rn "Compile Include.*client" server/ --include="*.csproj"` returning exactly one line). It compiles a 124-line, zero-`UnityEngine`-dependency C# file straight into the test project — not an asset, actual shared logic, tested by `SessionNonceTests.cs`-style xUnit tests elsewhere in that project. `client_web/tests/tutorial.test.ts`'s own header comment already explains why: the file was deliberately kept "so a change to the client's tutorial-blocking rules cannot silently diverge from what the tests assert," and Unity's abandonment does not retire it on its own — that comment explicitly defers the decision, this plan is what acts on it (by keeping the one file, not deleting it).

- [ ] **Step 4: Update the backlog entry**

  Edit `docs/architecture/NEXT_STEPS_BACKLOG.md`'s "OPEN BACKLOG ITEM, added 2026-09-17 - retire the Unity project" section: change "**Not started.**" to reference this plan, add the sixth reader (`FolkIdle.Server.Tests.csproj`'s `Compile Include` of `TutorialStateMachine.cs`) to the list, and note the two dev-only utility scripts (`tools/clean_sprites.py`, `tools/prepare_backgrounds.py`) found in Step 2. State the leave-in-place decision and point at this plan file.

- [ ] **Step 5: Commit**

  ```bash
  git add docs/architecture/NEXT_STEPS_BACKLOG.md
  git commit -m "docs(unity-retirement): confirm all six live readers of client/Assets before touching anything"
  ```

---

### Task 2: Establish the keep/delete manifest and prove every reader still works today

**Files:** none modified — this task only runs verification commands and records their output for Task 3 to act on. If any command fails, STOP; that failure is a defect in this plan's research, not something to work around.

**Interfaces:** none.

- [ ] **Step 1: Write down the manifest (in this plan file's own tracking, or as a scratch note — not a new repo file)**

  KEEP (survives Task 3, `client/Assets` unchanged in place):
  - `client/Assets/Images/Sprites/**` (648 MB — masters, `git`-LFS-tracked)
  - `client/Assets/Images/WithWhiteBackground/**` (809 MB — `tools/prepare_backgrounds.py`'s input)
  - `client/Assets/Images/SpritesWeb/**` (18 MB — served in production, `web.Dockerfile`, both `.csproj` files)
  - `client/Assets/Resources/Audio/*.wav` (11 files, 371 KB — `validate_audio.py`, `audio.ts`, `FolkIdle.Server.csproj`)
  - `client/Assets/Scripts/Engine/TutorialStateMachine.cs` (1 file — `FolkIdle.Server.Tests.csproj`'s `Compile Include`)

  DELETE (Task 3, all Unity-specific, zero readers found in Task 1):
  - `client/ProjectSettings/`, `client/Packages/`
  - `client/Assets/AddressableAssetsData/`, `Plugins/`, `Prefabs/`, `Scenes/`, `Settings/`, `StreamingAssets/`, `TextMesh Pro/`, `Tests/`
  - `client/Assets/csc.rsp` (a Unity compiler-response file, `-unsafe`; both server `.csproj` files already set `AllowUnsafeBlocks` independently)
  - `client/Assets/Scripts/Editor/`, `Scripts/Network/`, `Scripts/UI/`, and every file under `Scripts/Engine/` EXCEPT `TutorialStateMachine.cs`
  - every `*.meta` file anywhere under `client/` (Unity's asset-database metadata; none of the six confirmed readers touch a `.meta` file — they glob `*.wav`, `*.webp`, or name an exact `.cs` path)
  - `.github/workflows/unity_client.yml`

  Note: `client/Library/` does not exist in this checkout (gitignored, `.gitignore:11`) — nothing to delete there.

- [ ] **Step 2: Verify the sprite pipeline, unchanged, today**

  From `client_web/`:

  ```powershell
  npm run check:sprites
  ```

  Expected: passes (this is `generate-sprites.mjs --check`, which fails rather than silently rewriting `sprites.generated.ts` if the source tree doesn't match — confirms the generator can still find `client/Assets/Images/SpritesWeb` and everything it derives from).

- [ ] **Step 3: Verify the audio validator, unchanged, today**

  ```bash
  python3 ops/validate_audio.py
  ```

  Expected: reports the eleven real clips in `client/Assets/Resources/Audio`, no pointer-stub warnings.

- [ ] **Step 4: Verify both server `.csproj` files build, unchanged, today**

  Stop the running server first (CLAUDE.md's stale-build rule), then:

  ```powershell
  dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
  dotnet build server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  ```

  Expected: both succeed. The Tests build is the one that actually compiles `TutorialStateMachine.cs` via the `Compile Include` — a successful build here is the real proof the sixth reader resolves, not just the grep from Task 1.

- [ ] **Step 5: Verify the production Docker build, unchanged, today**

  From the repo root (the Dockerfile's own comment: "Build context is the REPOSITORY ROOT, not `client_web`"):

  ```bash
  docker build -f ops/oracle/web.Dockerfile -t folkidle-web-unity-retirement-check .
  ```

  Expected: succeeds, including its own internal check (`test -d dist/sprites && [ "$(find dist/sprites -type f | wc -l)" -gt 100 ]`) — this is the same assertion `web.Dockerfile` already makes to fail loudly on a missing-artwork deploy, and it's the most direct proof the production build path is unaffected. `VITE_FOLKIDLE_SERVER`/`FOLKIDLE_BUNDLE_VERSION` build args can be left unset for this local check; they don't affect whether the sprite `COPY` and the build succeed. Remove the test image afterward (`docker rmi folkidle-web-unity-retirement-check`) — it isn't meant to be deployed.

- [ ] **Step 6: Record the results**

  No code changes in this task. Report the pass/fail of Steps 2-5 to whoever reviews this plan before Task 3 proceeds — this is the "verify locally before the deletion task" evidence Global Constraints requires. If everything passed, Task 3 may proceed once a human has explicitly signed off (see Task 3's own header).

- [ ] **Step 7: Commit**

  Nothing to commit if Step 1's manifest lived only in this plan file — that's fine, this task is allowed to be a no-diff verification pass. If Step 1's manifest was also copied into a tracked file (e.g. appended to the same `NEXT_STEPS_BACKLOG.md` section Task 1 edited), commit that addition separately from Task 1's commit:

  ```bash
  git add docs/architecture/NEXT_STEPS_BACKLOG.md
  git commit -m "docs(unity-retirement): keep/delete manifest, all readers verified locally before deletion"
  ```

  (Skip this step entirely if there is no diff.)

---

### Task 3: Delete the Unity project — REQUIRES EXPLICIT HUMAN GO-AHEAD BEFORE MERGE/DEPLOY

**Do not merge or deploy this task's commit without an explicit go-ahead from the user, separate from and later than sign-off on Tasks 1-2.** This task deletes files from a repository that a live production Docker build and CI both read from, and Task 2's local verification — while necessary — is not the same guarantee as a green CI run and a real deploy. Land Tasks 1 and 2, get them reviewed, and stop. Only proceed with this task, and only merge/deploy it, on separate, explicit instruction.

**Files:**
- Delete: `client/ProjectSettings/`, `client/Packages/`
- Delete: `client/Assets/AddressableAssetsData/`, `client/Assets/Plugins/`, `client/Assets/Prefabs/`, `client/Assets/Scenes/`, `client/Assets/Settings/`, `client/Assets/StreamingAssets/`, `client/Assets/TextMesh Pro/`, `client/Assets/Tests/`, `client/Assets/csc.rsp`
- Delete: `client/Assets/Scripts/Editor/`, `client/Assets/Scripts/Network/`, `client/Assets/Scripts/UI/`
- Delete: every file under `client/Assets/Scripts/Engine/` except `TutorialStateMachine.cs`
- Delete: every `*.meta` file under `client/`
- Delete: `.github/workflows/unity_client.yml`
- Keep unchanged: `client/Assets/Images/**`, `client/Assets/Resources/Audio/**`, `client/Assets/Scripts/Engine/TutorialStateMachine.cs`

**Interfaces:** none — no reader's path changes (per the leave-in-place decision), so no `.csproj`, no `web.Dockerfile`, no `.mjs`/`.py` script needs an edit in this task.

- [ ] **Step 1: Delete the Unity-only top-level directories and files**

  ```bash
  git rm -r client/ProjectSettings client/Packages
  git rm -r "client/Assets/AddressableAssetsData" "client/Assets/Plugins" "client/Assets/Prefabs" "client/Assets/Scenes" "client/Assets/Settings" "client/Assets/StreamingAssets" "client/Assets/TextMesh Pro" "client/Assets/Tests"
  git rm "client/Assets/csc.rsp"
  ```

- [ ] **Step 2: Delete the Unity-only Scripts subtrees, keeping `TutorialStateMachine.cs`**

  ```bash
  git rm -r "client/Assets/Scripts/Editor" "client/Assets/Scripts/Network" "client/Assets/Scripts/UI"
  # Inside Scripts/Engine, remove everything except TutorialStateMachine.cs:
  find "client/Assets/Scripts/Engine" -type f ! -name "TutorialStateMachine.cs" -print -delete
  git add -A "client/Assets/Scripts/Engine"
  ```

  Confirm afterward: `ls client/Assets/Scripts/Engine` shows exactly `TutorialStateMachine.cs` (and, if not caught by the find above because `.meta` deletion is Step 3, `TutorialStateMachine.cs.meta` — that's fine, Step 3 removes it).

- [ ] **Step 3: Delete every remaining `.meta` file under `client/`**

  ```bash
  find client -name "*.meta" -print -delete
  git add -A client
  ```

  This also removes `TutorialStateMachine.cs.meta` — confirmed harmless, no reader touches `.meta` files (Task 1, Step 3).

- [ ] **Step 4: Delete the Unity CI workflow**

  ```bash
  git rm .github/workflows/unity_client.yml
  ```

- [ ] **Step 5: Confirm what survives matches the Task 2 manifest exactly**

  ```bash
  find client -type f | sort
  ```

  Expected: only files under `client/Assets/Images/**`, `client/Assets/Resources/Audio/**`, and the single `client/Assets/Scripts/Engine/TutorialStateMachine.cs`. Nothing else. If anything else remains or something expected is missing, stop and reconcile against Task 2's manifest before proceeding.

- [ ] **Step 6: Re-run every Task 2 verification, post-deletion**

  Same five checks, same expected results, run again now that the Unity project is gone:

  ```powershell
  # from client_web/
  npm run check:sprites
  ```
  ```bash
  python3 ops/validate_audio.py
  ```
  ```powershell
  dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj
  dotnet build server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  ```
  ```bash
  docker build -f ops/oracle/web.Dockerfile -t folkidle-web-unity-retirement-check-post .
  docker rmi folkidle-web-unity-retirement-check-post
  ```

  All five must pass with the same outcome as Task 2. A pass here is the actual proof this task is safe — Task 2's pre-deletion run is the baseline, this is the comparison.

- [ ] **Step 7: Run the full server test suite once**

  Stop the running server first, confirm Docker is up, then:

  ```powershell
  dotnet test server/FolkIdle.Server.Tests/FolkIdle.Server.Tests.csproj
  ```

  Expected: passes, including whatever test(s) exercise `TutorialStateMachine.cs`'s onboarding-blocking rules — this is the one place a silent break in the sixth reader would show up as a real test failure rather than just a successful compile.

- [ ] **Step 8: Commit**

  ```bash
  git add -A client .github/workflows/unity_client.yml
  git commit -m "chore(unity): delete the retired Unity project, keep the source art/audio and one shared script"
  ```

  Do not push, merge, or deploy this commit without the explicit go-ahead described at the top of this task.

- [ ] **Step 9: Update the backlog entry to closed**

  Once merged and deployed (not before): edit `docs/architecture/NEXT_STEPS_BACKLOG.md`'s "retire the Unity project" section one more time to mark it done, record the commit, and note that CI's `client` job and the Oracle deploy were both confirmed green afterward. Commit that separately:

  ```bash
  git add docs/architecture/NEXT_STEPS_BACKLOG.md
  git commit -m "docs(unity-retirement): close the backlog item, deployed and verified"
  ```

---

## Not in this plan

**Renaming `client/Assets` to a non-"client" top-level name** (e.g. `assets/`) is a reasonable follow-up once this plan's deletion has landed and been live for a while, but it touches every reader's path plus `.gitattributes`' scoped LFS exception for a cosmetic win on top of an already-risky change — deliberately deferred (see "Move vs. leave in place" above).

**Rewriting Git history to move the pre-LFS-migration blobs under `client/Assets/Images/Sprites`/`WithWhiteBackground` into actual LFS pointers** is a separate, already-known, already-documented repo hygiene issue (`.gitattributes`' own comment: "blobs already in history stay as plain objects until a deliberate history rewrite"). This plan's `git rm` of unrelated Unity files does not touch it, and no task here should be repurposed to fix it.

**`tools/clean_sprites.py` and `tools/prepare_backgrounds.py`** are confirmed (Task 1) to read/write real paths under `client/Assets/Images` but are dev-only utility scripts, not part of `npm run build`, `npm run generate:sprites`, or CI. They need no changes under the leave-in-place decision. If a future plan moves the asset tree, update these two scripts' hardcoded paths alongside the six confirmed readers.

"""Refuses `dotnet build` / `dotnet test` while the game server is running.

This is the repo's oldest silent trap, recorded in run-dev.ps1 and CLAUDE.md:
a running server holds the build output directory, so the build SUCCEEDS and
emits a stale DLL. Nothing fails and nothing warns - the next run is simply the
previous build with none of your changes in it, which then gets debugged as a
code problem.

Prose could not stop this, because the rule has to be remembered at exactly the
moment attention is elsewhere. A hook does not have to remember.

Modul: ONLY THE SAME CHECKOUT (2026-10-29). The lock is on one checkout's own
bin/ directory, so a server started from worktree A cannot make a build in
worktree B stale. Blocking every build machine-wide serialised work that has
no reason to wait - a ten-minute `exercise` against one worktree held up the
ten-minute test suite in another. The hook now resolves which checkout each
running server came from and which checkout the command targets, and blocks
only when they are the same. When it cannot tell, it blocks, as before.

Written in Python rather than shell because this machine has no `jq`.
Reads a PreToolUse payload on stdin, answers on stdout.
"""

import json
import os
import re
import subprocess
import sys

SERVER_IMAGE = "FolkIdle.Server.exe"

# A Windows path (C:\x or C:/x) or a Git Bash one (/c/x), up to the next
# shell separator or quote.
PATH_PATTERN = re.compile(r"(?:[A-Za-z]:[\\/]|/[A-Za-z]/)[^\s'\"`;&|<>]+")


def running_server_paths():
    """Executable paths of running servers; None if a server runs but its path is unknown; [] if none."""
    try:
        listing = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {SERVER_IMAGE}"],
            capture_output=True,
            text=True,
            timeout=10,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        # Modul: fail OPEN. A hook that cannot tell whether anything runs must
        # not stand between the developer and their build - a false block is
        # worse than the trap it guards, because it has no workaround.
        return []
    if SERVER_IMAGE.lower() not in listing.lower():
        return []

    try:
        out = subprocess.run(
            [
                "powershell",
                "-NoProfile",
                "-Command",
                "Get-CimInstance Win32_Process -Filter \"Name='FolkIdle.Server.exe'\" | "
                "ForEach-Object { $_.ExecutablePath }",
            ],
            capture_output=True,
            text=True,
            timeout=15,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    paths = [line.strip() for line in out.splitlines() if line.strip()]
    return paths or None


def normalise(path: str) -> str:
    """C:\\x, C:/x and /c/x all become c:/x, lower-case, no trailing slash."""
    path = path.strip().strip("'\"").replace("\\", "/")
    match = re.match(r"^/([A-Za-z])/(.*)$", path)
    if match:
        path = f"{match.group(1)}:/{match.group(2)}"
    return path.rstrip("/").lower()


def checkout_root(path: str):
    """The nearest ancestor holding a .git entry (a worktree's .git is a file), or None."""
    current = normalise(path)
    while current:
        if os.path.exists(current + "/.git"):
            return current
        parent = current.rsplit("/", 1)[0] if "/" in current else ""
        if parent == current or not parent or parent.endswith(":"):
            return None
        current = parent
    return None


def command_targets(command: str, cwd: str):
    """The checkouts a command builds in: every path it names, else its cwd."""
    roots = {checkout_root(p) for p in PATH_PATTERN.findall(command)}
    roots.discard(None)
    if roots:
        return roots
    root = checkout_root(cwd) if cwd else None
    return {root} if root else set()


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0

    command = (payload.get("tool_input") or {}).get("command") or ""

    # `dotnet run` is how the server is STARTED - never block it. Only the two
    # verbs that write into the locked output directory.
    if "dotnet build" not in command and "dotnet test" not in command:
        return 0

    servers = running_server_paths()
    if servers == []:
        return 0

    reason = None
    if servers is None:
        reason = "it is running and its checkout could not be resolved"
    else:
        server_roots = {checkout_root(p) for p in servers}
        targets = command_targets(command, payload.get("cwd") or "")
        if None in server_roots or not targets:
            reason = "it is running and the hook could not tell which checkout it or this command belongs to"
        else:
            clash = sorted(server_roots & targets)
            if clash:
                reason = f"it is running from {clash[0]}, the same checkout this command builds in"

    if reason is None:
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    f"{SERVER_IMAGE}: {reason}. It holds that checkout's build "
                    "output directory, so this build would SUCCEED while silently "
                    "producing a stale DLL. Stop it first (a build in a DIFFERENT "
                    "worktree is allowed):\n\n"
                    f"  taskkill //F //IM {SERVER_IMAGE}\n\n"
                    "or: Get-Process dotnet | Stop-Process -Force"
                ),
            }
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

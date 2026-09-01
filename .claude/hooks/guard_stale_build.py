"""Refuses `dotnet build` / `dotnet test` while the game server is running.

This is the repo's oldest silent trap, recorded in run-dev.ps1 and CLAUDE.md:
a running server holds the build output directory, so the build SUCCEEDS and
emits a stale DLL. Nothing fails and nothing warns - the next run is simply the
previous build with none of your changes in it, which then gets debugged as a
code problem.

Prose could not stop this, because the rule has to be remembered at exactly the
moment attention is elsewhere. A hook does not have to remember.

Written in Python rather than shell because this machine has no `jq`.
Reads a PreToolUse payload on stdin, answers on stdout.
"""

import json
import subprocess
import sys

SERVER_IMAGE = "FolkIdle.Server.exe"


def server_is_running() -> bool:
    try:
        out = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {SERVER_IMAGE}"],
            capture_output=True,
            text=True,
            timeout=10,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        # Modul: fail OPEN. A hook that cannot tell must not stand between the
        # developer and their build - a false block is worse than the trap it
        # guards, because it has no workaround.
        return False
    return SERVER_IMAGE.lower() in out.lower()


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

    if not server_is_running():
        return 0

    json.dump(
        {
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    f"{SERVER_IMAGE} is running. It holds the build output "
                    "directory, so this build would SUCCEED while silently "
                    "producing a stale DLL. Stop it first:\n\n"
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

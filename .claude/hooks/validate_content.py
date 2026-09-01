"""Runs the content validator the moment a GameData JSON file is written.

ops/validate_content.py enforces the same rules ContentRegistry.Initialize and
ActiveSkillEngine.Initialize enforce at server boot. CI already gates on it -
this moves the finding from "the pipeline failed some minutes later" to "that
edit was wrong", which is where it is cheapest to fix. A malformed content file
otherwise surfaces as a crash-looping server.

Blocks on failure so the content is corrected rather than carried forward.
Reads a PostToolUse payload.
"""

import json
import subprocess
import sys
from pathlib import Path


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0

    path = (payload.get("tool_response") or {}).get("filePath") or (
        payload.get("tool_input") or {}
    ).get("file_path") or ""

    normalised = path.replace("\\", "/")
    if "server/GameData/" not in normalised or not normalised.endswith(".json"):
        return 0

    # The hook's own location is the reliable way back to the repo root; the
    # working directory a hook inherits is not guaranteed.
    root = Path(__file__).resolve().parents[2]
    validator = root / "ops" / "validate_content.py"
    if not validator.exists():
        return 0

    try:
        result = subprocess.run(
            [sys.executable, str(validator), "--path", "server/GameData"],
            cwd=str(root),
            capture_output=True,
            text=True,
            timeout=60,
        )
    except (OSError, subprocess.SubprocessError):
        # Modul: fail OPEN, as in guard_stale_build. A validator that cannot be
        # run is not evidence that the content is bad.
        return 0

    if result.returncode == 0:
        return 0

    output = (result.stdout + result.stderr).strip()
    json.dump(
        {
            "decision": "block",
            "reason": (
                "server/GameData content validation FAILED after this edit. The "
                "server enforces these same rules at boot, so this would "
                "crash-loop rather than start.\n\n" + output
            ),
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

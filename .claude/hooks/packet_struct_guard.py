"""Fires when a WIRE STRUCT is edited.

Two separate things then have to move in the same commit, and neither fails
loudly if forgotten:

1. The size constant in NetworkPacketLayoutGuard. It throws on STARTUP if the
   struct disagrees - so forgetting costs a server that will not boot,
   discovered much later than it should be.
2. client_web/src/lib/net/protocol.generated.ts, via `npm run generate:protocol`.
   The client's copy drifted once already and threw on every startup.

StateUpdatePacket also sits near a ~700-byte structural ceiling, which is worth
knowing BEFORE adding a field rather than after.

Advisory only - never blocks an edit. Reads a PostToolUse payload.
"""

import json
import sys

WIRE_STRUCTS = ("clientcommandpacket.cs", "stateupdatepacket.cs")

GUIDANCE = (
    "You edited a wire struct. If you added, removed or resized a FIELD, two "
    "things must move in this same commit or the server throws on startup:\n"
    "1. The matching constant in "
    "server/FolkIdle.Server/Network/NetworkPacketLayoutGuard.cs "
    "(ExpectedClientCommandSize / ExpectedStateUpdateSize) - and add your delta "
    "to its byte-by-byte comment history, which is how the ceiling stays "
    "auditable.\n"
    "2. `npm run generate:protocol` in client_web/ to regenerate "
    "protocol.generated.ts. Never hand-edit that file.\n"
    "StateUpdatePacket is near its ~700-byte ceiling - read the guard's "
    "comments for current headroom before growing it. And if you only needed to "
    "cache something per-player, TickStatePayload is NOT the wire: it costs "
    "nothing here and needs no regeneration."
)


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, ValueError):
        return 0

    path = (payload.get("tool_response") or {}).get("filePath") or (
        payload.get("tool_input") or {}
    ).get("file_path") or ""

    normalised = path.replace("\\", "/").lower()
    if not normalised.endswith(WIRE_STRUCTS):
        return 0

    json.dump(
        {
            "systemMessage": (
                "Wire struct edited - the packet size guard and the generated "
                "client protocol may both need updating."
            ),
            "hookSpecificOutput": {
                "hookEventName": "PostToolUse",
                "additionalContext": GUIDANCE,
            },
        },
        sys.stdout,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

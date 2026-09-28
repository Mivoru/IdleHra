#!/usr/bin/env python3
"""Pre-build check that the served audio clips are real audio, not LFS stubs.

WHY THIS EXISTS. The eleven clips in client/Assets/Resources/Audio are the
only sound the game has. FolkIdle.Server.csproj links them into the publish
output and NetworkBroadcastSystem serves them at /audio/<name>.wav. They used
to be tracked in Git LFS, git-lfs is not installed on the Oracle deploy box,
and so the box's `git pull` produced 130-byte pointer stubs. The build copied
the stubs, the server served them with Content-Type: audio/wav, the browser's
decodeAudioData rejected them, client_web/src/lib/ui/audio.ts recorded a miss
and moved on. The game was silent in production for its entire life and not one
layer said a word - exercise.mjs even counts absent clips as expected 404s.

WHY *HERE*, AND NOT IN A TEST OR AT SERVER STARTUP. This failure is a
CHECKOUT-TIME failure, not a runtime one: the bytes are already wrong before
anything is built. GitHub Actions checks out without LFS (actions/checkout
defaults to lfs: false), which means CI reproduces the deploy box exactly - so
a plain file check in the pipeline sees precisely what production would ship.
A server-side assertion would only fire after a deploy, an xUnit test needs
Docker and a Postgres container to say something that has nothing to do with
the database, and the sprite generator does not look at audio at all. This is
seconds, needs nothing installed, and fails on the exact regression.

The paired guard is in server/Dockerfile, which re-checks the *publish output*
- that catches the other half, an MSBuild glob that stops matching.

Exit code 0 on success, 1 on any violation (all listed, not just the first).

Usage:
    python3 ops/validate_audio.py [--path client/Assets/Resources/Audio]
"""

import argparse
import os
import sys

# Modul: the smallest real clip is ui_button_click.wav at 4012 bytes, and an
# LFS pointer is ~130. Anything under a kilobyte is a stub or a truncation, not
# a sound - the threshold is deliberately far from both numbers so it needs no
# maintenance when a clip is re-rendered.
MINIMUM_PLAUSIBLE_BYTES = 1024

LFS_POINTER_PREFIX = b"version https://git-lfs.github.com/spec/"

# Every clip the client can ask for and that must exist. combat_hit_crit.wav
# is NOT here on purpose: it has never been authored and audio.ts falls back
# for it by design. The rest of the once-optional clips arrived 2026-09-27.
REQUIRED_CLIPS = [
    "achievement_unlock.wav",
    "combat_boss_first_clear.wav",
    "combat_hit_magic.wav",
    "combat_hit_melee.wav",
    "combat_hit_ranged.wav",
    "combat_miss.wav",
    "combat_player_died.wav",
    "combat_player_died_female.wav",
    "crafting_completed.wav",
    "delve_door_open.wav",
    "error.wav",
    "item_equipped.wav",
    "item_sold.wav",
    "level_up.wav",
    "loot_rare_dropped.wav",
    "notification.wav",
    "race_unlocked.wav",
    "ui_button_click.wav",
    "ui_window_close.wav",
    "ui_window_open.wav",
]


def validate_clip(directory, file_name, errors):
    path = os.path.join(directory, file_name)

    if not os.path.isfile(path):
        errors.append(f"{file_name}: missing")
        return

    size = os.path.getsize(path)

    with open(path, "rb") as handle:
        head = handle.read(64)

    if head.startswith(LFS_POINTER_PREFIX):
        errors.append(
            f"{file_name}: is a Git LFS POINTER STUB ({size} bytes), not audio. "
            "Something re-added *.wav to LFS - see the last rule in .gitattributes."
        )
        return

    # RIFF....WAVE. Checked rather than trusting the extension, because the
    # failure this guards against is a file whose name is right and whose
    # contents are not.
    if head[0:4] != b"RIFF" or head[8:12] != b"WAVE":
        errors.append(f"{file_name}: not a RIFF/WAVE file ({size} bytes, starts {head[0:12]!r})")
        return

    if size < MINIMUM_PLAUSIBLE_BYTES:
        errors.append(f"{file_name}: implausibly small at {size} bytes (expected > {MINIMUM_PLAUSIBLE_BYTES})")


def main():
    parser = argparse.ArgumentParser(description="Validate the served audio clips are real audio.")
    parser.add_argument("--path", default=os.path.join("client", "Assets", "Resources", "Audio"))
    args = parser.parse_args()

    errors = []

    if not os.path.isdir(args.path):
        print(f"validate_audio: audio directory '{args.path}' does not exist.")
        return 1

    for file_name in REQUIRED_CLIPS:
        validate_clip(args.path, file_name, errors)

    # The background music (owner, 2026-09-28): every track in Music/ must be a
    # real MP3 - an ID3 tag or an MPEG frame sync - not a Git LFS pointer, and
    # there must be at least one, or the game plays silence without a word.
    music_dir = os.path.join(args.path, "Music")
    tracks = sorted(f for f in os.listdir(music_dir) if f.endswith(".mp3")) if os.path.isdir(music_dir) else []
    if not tracks:
        errors.append("Music/: no .mp3 tracks - the background music would be silent")
    for track in tracks:
        path = os.path.join(music_dir, track)
        size = os.path.getsize(path)
        with open(path, "rb") as handle:
            head = handle.read(64)
        if head.startswith(LFS_POINTER_PREFIX):
            errors.append(f"Music/{track}: is a Git LFS POINTER STUB ({size} bytes) - see the music rule in .gitattributes.")
        elif not (head[0:3] == b"ID3" or (head[0] == 0xFF and (head[1] & 0xE0) == 0xE0)):
            errors.append(f"Music/{track}: not an MP3 ({size} bytes, starts {head[0:4]!r})")
        elif size < 100_000:
            errors.append(f"Music/{track}: implausibly small for a track at {size} bytes")

    if errors:
        print(f"validate_audio: {len(errors)} violation(s) found in '{args.path}':")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"validate_audio: all {len(REQUIRED_CLIPS)} clips in '{args.path}' are real WAV data, and {len(tracks)} music track(s) are real MP3s.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

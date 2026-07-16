#!/usr/bin/env python3
"""Pre-build content validation for server/GameData/*.json.

Mirrors the structural rules ContentRegistry.Initialize and
ActiveSkillEngine.Initialize enforce at server boot (see
server/FolkIdle.Server/Engine/ContentRegistry.cs and ActiveSkillEngine.cs),
so a malformed content file fails the CI pipeline before a broken image is
built and pushed, instead of only surfacing as a crash-looping pod at
rollout. The server's own "--validate-content" flag runs the authoritative
C# parse path; this script exists so the check can run in seconds, before
any dotnet build, and its rules must be kept in sync with the C# side:

  monsters.json:        Ids exactly 1..N contiguous, no duplicates;
                        MaxHp > 0; AttackIntervalMs > 0;
                        Name and EnemyId non-empty.
  items.json:           Ids exactly 1..N contiguous, no duplicates;
                        BaseId non-empty.
  gathering_nodes.json: no duplicate ActivityId.
  skills.json:          exactly MAX_SKILL_ID entries, SkillId exactly
                        1..MAX_SKILL_ID, no duplicates; ManaCost,
                        CooldownMs, DamageMultiplierPct and
                        RequiredSkillPointCost all > 0.
  GameBalanceConfig.json: optional (a missing file falls back to
                        ContentRegistry's built-in defaults, same as the
                        C# side) - if present, must parse as a single JSON
                        object, not a list.

Exit code 0 on success, 1 on any violation (with every violation listed,
not just the first, so a content author fixes one CI round trip, not N).

Usage:
    python3 ops/validate_content.py [--path server/GameData]
"""

import argparse
import json
import os
import sys

# Must match ActiveSkillEngine.MaxSkillId.
MAX_SKILL_ID = 4

REQUIRED_FILES = ["monsters.json", "items.json", "gathering_nodes.json", "skills.json"]


def load_json_list(path, errors):
    """Parse a content file, mirroring ReadAndValidateJsonFile: the file
    must exist, parse as JSON, and be a non-empty list."""
    if not os.path.isfile(path):
        errors.append(f"{os.path.basename(path)}: required content file not found at '{path}'.")
        return None
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            parsed = json.load(handle)
    except json.JSONDecodeError as ex:
        errors.append(f"{os.path.basename(path)}: malformed JSON: {ex}.")
        return None
    if not isinstance(parsed, list) or len(parsed) == 0:
        errors.append(f"{os.path.basename(path)}: parsed to null or an empty list - at least one content entry is required.")
        return None
    return parsed


def require_contiguous_ids(entries, file_name, id_field, errors):
    """Mirrors ContentRegistry.RequireContiguousIds: IDs must be exactly
    1..N with no gaps or duplicates, since content is indexed by Id-1."""
    count = len(entries)
    seen = set()
    for entry in entries:
        entry_id = entry.get(id_field)
        if not isinstance(entry_id, int) or entry_id < 1 or entry_id > count:
            errors.append(f"{file_name}: {id_field} ({entry_id}) outside the required contiguous range 1..{count}.")
            continue
        if entry_id in seen:
            errors.append(f"{file_name}: duplicate {id_field} ({entry_id}).")
        seen.add(entry_id)


def validate_monsters(entries, errors):
    require_contiguous_ids(entries, "monsters.json", "Id", errors)
    for entry in entries:
        entry_id = entry.get("Id")
        if not isinstance(entry.get("MaxHp"), (int, float)) or entry.get("MaxHp", 0) <= 0:
            errors.append(f"monsters.json: entry Id={entry_id} has non-positive MaxHp ({entry.get('MaxHp')}).")
        if not isinstance(entry.get("AttackIntervalMs"), (int, float)) or entry.get("AttackIntervalMs", 0) <= 0:
            errors.append(f"monsters.json: entry Id={entry_id} has non-positive AttackIntervalMs ({entry.get('AttackIntervalMs')}).")
        if not entry.get("Name") or not entry.get("EnemyId"):
            errors.append(f"monsters.json: entry Id={entry_id} is missing Name or EnemyId.")


def validate_items(entries, errors):
    require_contiguous_ids(entries, "items.json", "Id", errors)
    for entry in entries:
        if not entry.get("BaseId"):
            errors.append(f"items.json: entry Id={entry.get('Id')} is missing BaseId.")


def validate_gathering_nodes(entries, errors):
    seen = set()
    for entry in entries:
        activity_id = entry.get("ActivityId")
        if activity_id in seen:
            errors.append(f"gathering_nodes.json: duplicate ActivityId ({activity_id}).")
        seen.add(activity_id)


def validate_skills(entries, errors):
    if len(entries) != MAX_SKILL_ID:
        errors.append(f"skills.json: must contain exactly {MAX_SKILL_ID} entries, found {len(entries)}.")
    seen = set()
    for entry in entries:
        skill_id = entry.get("SkillId")
        if not isinstance(skill_id, int) or skill_id < 1 or skill_id > MAX_SKILL_ID:
            errors.append(f"skills.json: SkillId ({skill_id}) outside the required range 1..{MAX_SKILL_ID}.")
            continue
        if skill_id in seen:
            errors.append(f"skills.json: duplicate SkillId ({skill_id}).")
        seen.add(skill_id)
        for field in ("ManaCost", "CooldownMs", "DamageMultiplierPct", "RequiredSkillPointCost"):
            value = entry.get(field)
            if not isinstance(value, (int, float)) or value <= 0:
                errors.append(f"skills.json: SkillId={skill_id} has a non-positive {field} ({value}).")


def validate_balance_config(path, errors):
    """Optional single-object file - only validated if present."""
    if not os.path.isfile(path):
        return
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            parsed = json.load(handle)
    except json.JSONDecodeError as ex:
        errors.append(f"GameBalanceConfig.json: malformed JSON: {ex}.")
        return
    if not isinstance(parsed, dict):
        errors.append("GameBalanceConfig.json: must be a single JSON object, not a list.")


def main():
    parser = argparse.ArgumentParser(description="Validate FolkIdle GameData content files.")
    parser.add_argument("--path", default=os.path.join("server", "GameData"),
                        help="Path to the GameData directory (default: server/GameData).")
    args = parser.parse_args()

    errors = []
    validate_balance_config(os.path.join(args.path, "GameBalanceConfig.json"), errors)

    if not os.path.isdir(args.path):
        print(f"validate_content: GameData directory not found at '{args.path}'.")
        return 1

    parsed = {}
    for file_name in REQUIRED_FILES:
        parsed[file_name] = load_json_list(os.path.join(args.path, file_name), errors)

    if parsed["monsters.json"] is not None:
        validate_monsters(parsed["monsters.json"], errors)
    if parsed["items.json"] is not None:
        validate_items(parsed["items.json"], errors)
    if parsed["gathering_nodes.json"] is not None:
        validate_gathering_nodes(parsed["gathering_nodes.json"], errors)
    if parsed["skills.json"] is not None:
        validate_skills(parsed["skills.json"], errors)

    if errors:
        print(f"validate_content: {len(errors)} violation(s) found in '{args.path}':")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"validate_content: all {len(REQUIRED_FILES)} content files in '{args.path}' passed validation.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

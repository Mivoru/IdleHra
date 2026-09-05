// The fight log's rules, without a DOM.
//
// Two things are worth pinning here and neither is cosmetic:
//
//  1. The wording names only mechanics the server actually resolves. The
//     original request asked for "whether the attack was blocked", and block
//     is real - but only on INCOMING hits, because the player has
//     BlockStrengthPct (CON-derived) and monsters have no block stat at all.
//     A log that said "blocked" about a player's swing would be teaching a
//     stat the game does not have. Armour is never a line of its own: it
//     reduces every hit rather than stopping any, so it is already inside the
//     number shown.
//  2. The sequence guard. A reconnect can redeliver events the log already
//     holds, and a duplicated "Critical! 861" reads as two crits rather than
//     one packet arriving twice.
import { describe, it, expect, beforeEach } from 'vitest';
import { get } from 'svelte/store';
import {
  combatLog,
  pushCombatEvent,
  resetCombatLog,
  describeCombatLine,
  pushLootLine,
  CombatEventKind,
  CombatEventFlag,
  LOOT_LINE_KIND,
  LootDropKind,
  MAX_LOG_LINES,
  type CombatLogLine,
} from '../src/lib/stores/combatLog';
import { PacketType, type ResponseCombatEvent } from '../src/lib/net/protocol.generated';

function event(partial: Partial<ResponseCombatEvent>): ResponseCombatEvent {
  return {
    type: PacketType.ResponseCombatEvent,
    PlayerId: 1,
    MonsterId: 91,
    Amount: 0,
    MonsterHpAfter: 0,
    Sequence: 1,
    EventKind: CombatEventKind.PlayerHit,
    Flags: 0,
    ...partial,
  } as ResponseCombatEvent;
}

function line(partial: Partial<CombatLogLine>): CombatLogLine {
  return {
    id: 1,
    kind: CombatEventKind.PlayerHit,
    amount: 0,
    monsterId: 91,
    monsterHpAfter: 0,
    flags: 0,
    atMs: 0,
    ...partial,
  };
}

describe('the fight log', () => {
  beforeEach(() => resetCombatLog());

  it('keeps the newest line first', () => {
    pushCombatEvent(event({ Sequence: 1, Amount: 10 }));
    pushCombatEvent(event({ Sequence: 2, Amount: 20 }));

    const lines = get(combatLog);
    expect(lines[0].amount).toBe(20);
    expect(lines[1].amount).toBe(10);
  });

  it('is a log and not a ledger', () => {
    for (let i = 1; i <= MAX_LOG_LINES + 25; i++) {
      pushCombatEvent(event({ Sequence: i, Amount: i }));
    }

    const lines = get(combatLog);
    expect(lines).toHaveLength(MAX_LOG_LINES);
    expect(lines[0].amount).toBe(MAX_LOG_LINES + 25);
  });

  it('drops an event it has already shown, so a reconnect does not double every hit', () => {
    pushCombatEvent(event({ Sequence: 7, Amount: 100 }));
    pushCombatEvent(event({ Sequence: 7, Amount: 100 }));
    pushCombatEvent(event({ Sequence: 6, Amount: 100 }));

    expect(get(combatLog)).toHaveLength(1);
  });

  it('starts numbering again after a reset, because a new session does too', () => {
    pushCombatEvent(event({ Sequence: 900 }));
    resetCombatLog();
    pushCombatEvent(event({ Sequence: 1, Amount: 42 }));

    const lines = get(combatLog);
    expect(lines).toHaveLength(1);
    expect(lines[0].amount).toBe(42);
  });
});

describe('loot in the fight log', () => {
  beforeEach(() => resetCombatLog());

  // Modul: REPORTED AS "IT LOOKS LIKE NO ITEMS ARE DROPPING".
  //
  // They were. The database showed 31 recent drops on that account and the
  // server log showed the dispatch. What was missing is that this log narrated
  // the hits, the misses, the lifesteal and the kill - and said nothing at all
  // about the reward. Somebody reading a live account of a fight that never
  // mentions loot will conclude there is none, and they are right to.
  it('puts a drop in the same place the fight is', () => {
    pushLootLine(250, 3, 0, LootDropKind.Material);

    const lines = get(combatLog);
    expect(lines).toHaveLength(1);
    expect(lines[0].kind).toBe(LOOT_LINE_KIND);
    expect(lines[0].lootItemId).toBe(250);
    expect(lines[0].amount).toBe(3);
  });

  it('names the item, and the caller resolves it - the store has no registry', () => {
    const material = line({ kind: LOOT_LINE_KIND, amount: 3, lootDropKind: LootDropKind.Material });
    expect(describeCombatLine(material, 'Mouse Fur')).toBe('Dropped: Mouse Fur x3');

    // Equipment is one piece, so a quantity would be noise.
    const gear = line({ kind: LOOT_LINE_KIND, amount: 1, lootDropKind: LootDropKind.Equipment });
    expect(describeCombatLine(gear, 'Steel Harness')).toBe('Dropped: Steel Harness');

    const scrap = line({ kind: LOOT_LINE_KIND, amount: 12, lootDropKind: LootDropKind.Scrap });
    expect(describeCombatLine(scrap, 'Iron Ore')).toBe('Salvaged into Iron Ore x12');
  });

  it('cannot collide with a combat event id however long the session runs', () => {
    // Loot has no sequence of its own - it arrives on a different packet - so
    // it counts DOWN from zero while the server counts up.
    pushLootLine(250, 1, 0, LootDropKind.Material);
    pushLootLine(251, 1, 0, LootDropKind.Material);
    pushCombatEvent(event({ Sequence: 1 }));

    const ids = get(combatLog).map((l) => l.id);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids.filter((i) => i < 0)).toHaveLength(2);
  });

  it('shares the ring buffer with the blows, so a busy fight cannot flood it', () => {
    for (let i = 0; i < MAX_LOG_LINES + 10; i++) pushLootLine(250, 1, 0, LootDropKind.Material);
    expect(get(combatLog)).toHaveLength(MAX_LOG_LINES);
  });
});

describe('what a line says', () => {
  it('reports a hit, and marks a crit', () => {
    expect(describeCombatLine(line({ amount: 412 }), 'Field Mouse')).toBe('You hit Field Mouse for 412');
    expect(describeCombatLine(line({ amount: 861, flags: CombatEventFlag.Crit }), 'Field Mouse')).toBe(
      'Critical! You hit Field Mouse for 861',
    );
  });

  it('reports a miss, which no health difference could ever have implied', () => {
    expect(describeCombatLine(line({ kind: CombatEventKind.PlayerMiss }), 'Field Mouse')).toBe('You miss Field Mouse');
    expect(describeCombatLine(line({ kind: CombatEventKind.MonsterMiss }), 'Field Mouse')).toBe(
      'Field Mouse misses you',
    );
  });

  it('says blocked only about an incoming hit', () => {
    const incoming = describeCombatLine(
      line({ kind: CombatEventKind.MonsterHit, amount: 8, flags: CombatEventFlag.Blocked }),
      'Field Mouse',
    );
    expect(incoming).toBe('Field Mouse hits you for 8 (blocked)');

    // The same flag on a player's swing must not produce the word: monsters
    // carry no block stat, so it could only ever be armour mislabelled.
    const outgoing = describeCombatLine(line({ amount: 412, flags: CombatEventFlag.Blocked }), 'Field Mouse');
    expect(outgoing).not.toContain('blocked');
  });

  it('names lifesteal, burn, thorns and the kill', () => {
    expect(describeCombatLine(line({ kind: CombatEventKind.Lifesteal, amount: 4 }), 'Field Mouse')).toBe(
      'Lifesteal heals you for 4',
    );
    expect(describeCombatLine(line({ amount: 500, flags: CombatEventFlag.Burn }), 'Field Mouse')).toBe(
      'You hit Field Mouse for 500 (burning)',
    );
    expect(describeCombatLine(line({ amount: 12, flags: CombatEventFlag.Thorns }), 'Field Mouse')).toBe(
      'Thorns reflect 12',
    );
    expect(describeCombatLine(line({ kind: CombatEventKind.Kill, amount: 93 }), 'Field Mouse')).toContain(
      'Field Mouse dies',
    );
  });
});

// Modul: the integration test that matters. It drives the REAL client modules -
// auth.ts, connection.ts, the generated protocol - against a REAL running
// server, rather than a hand-written harness that could quietly disagree with
// the client it is supposed to be validating.
//
// The headline assertion is survival past 60 seconds. Both undocumented wire
// obligations kill a session with the same silent symptom (close 1008, no
// server log), and the anti-cheat one only fires after four missed challenges
// at 15 s each - so a short test passes while looking completely healthy. This
// test is deliberately slow because the bug it guards is deliberately slow.
//
// Opt-in: it needs Postgres, Redis and the server. `npm test` stays fast.
//   FOLKIDLE_LIVE=1 npx vitest run tests/live.integration.test.ts

import { describe, it, expect, beforeAll } from 'vitest';

const LIVE = process.env.FOLKIDLE_LIVE === '1';
const BASE = process.env.VITE_FOLKIDLE_SERVER ?? 'http://localhost:8080';

// connection.ts needs no storage, but auth.ts does; Node has neither.
function installStorageShims(): void {
  const make = () => {
    const map = new Map<string, string>();
    return {
      getItem: (k: string) => map.get(k) ?? null,
      setItem: (k: string, v: string) => void map.set(k, v),
      removeItem: (k: string) => void map.delete(k),
      clear: () => map.clear(),
      key: (i: number) => [...map.keys()][i] ?? null,
      get length() {
        return map.size;
      },
    } as Storage;
  };
  Object.assign(globalThis, { sessionStorage: make(), localStorage: make() });
}

async function serverIsUp(): Promise<boolean> {
  try {
    const response = await fetch(`${BASE}/healthz`, { signal: AbortSignal.timeout(2000) });
    return response.ok;
  } catch {
    return false;
  }
}

describe.skipIf(!LIVE)('live server integration', () => {
  beforeAll(async () => {
    installStorageShims();
    if (!(await serverIsUp())) {
      throw new Error(`no server at ${BASE} - start it before running with FOLKIDLE_LIVE=1`);
    }
  });

  it('serves the content files a client needs', async () => {
    const manifest = await (await fetch(`${BASE}/gamedata`)).json();
    expect(manifest.Files).toContain('monsters.json');

    const { loadContent } = await import('../src/lib/net/content');
    const registry = await loadContent();

    // Content canon: exactly five regions of five (four plus a boss), ids
    // 91-115. Asserted because deriving this wrongly has caused bugs before.
    expect(registry.regions).toHaveLength(5);
    for (const region of registry.regions) expect(region).toHaveLength(5);
    expect(registry.regions[0][0].Id).toBe(91);
    expect(registry.regions[4][4].Id).toBe(115);
    expect(registry.regions[4][4].Name).toBe('Malakor');
  });

  it(
    'stays connected past the anti-cheat deadline and plays the game',
    async () => {
      const { loginWithDevice } = await import('../src/lib/net/auth');
      const { GameConnection } = await import('../src/lib/net/connection');
      const { CommandType } = await import('../src/lib/net/protocol.generated');

      const session = await loginWithDevice();
      expect(session.token.split('.')).toHaveLength(3);

      const connection = new GameConnection();

      let stateUpdates = 0;
      let becameLive = false;
      let terminatedDetail = '';
      let sawChallenge = false;
      let reachedCombat = false;
      let armed = false;
      let firstXp: number | null = null;
      let lastXp = 0;

      await new Promise<void>((resolve) => {
        // 75 s: comfortably past the ~60 s an unanswered-challenge quarantine
        // takes (15 s window x 4 consecutive misses).
        const finish = setTimeout(() => resolve(), 75_000);

        connection.connect(session.token, {
          onStatus: (status) => {
            if (status.phase === 'live') becameLive = true;
            if (status.phase === 'reconnecting') {
              terminatedDetail = status.detail;
              clearTimeout(finish);
              resolve();
            }
          },
          onStateUpdate: (packet) => {
            stateUpdates++;
            if (packet.ActiveChallengeSeed !== 0) sawChallenge = true;
            if (packet.ActiveActivityId === 91) reachedCombat = true;
            if (firstXp === null) firstXp = packet.CurrentXp;
            lastXp = packet.CurrentXp;

            if (!armed) {
              armed = true;
              // Auto-eat off so an empty larder cannot halt a fresh account
              // after one kill, then fight the first canonical monster.
              connection.send({ Command: CommandType.UpdateAutoEatThreshold, TargetId: 0 });
              setTimeout(
                () => connection.send({ Command: CommandType.ChangeActivity, TargetId: 91 }),
                500,
              );
            }
          },
        });
      });

      connection.disconnect();

      // The whole point: a client that fails either obligation is dead by now.
      expect(terminatedDetail).toBe('');
      expect(becameLive).toBe(true);
      expect(stateUpdates).toBeGreaterThan(50);

      // Proves the server actually issued challenges during the run, so the
      // survival above is evidence of answering them rather than evidence that
      // none were asked.
      expect(sawChallenge).toBe(true);

      // The commands were accepted rather than silently dropped, and the
      // fight actually resolved kills.
      //
      // Deliberately NOT asserting loot here. A level-1 character's drops are
      // a 35% roll per kill, so a run with none is unlucky rather than broken -
      // and a test that fails on bad luck teaches people to re-run it, which
      // is worse than not having it. Loot is asserted below, on the fixture
      // whose damage output makes it reliable.
      expect(reachedCombat).toBe(true);
      expect(lastXp).toBeGreaterThan(firstXp ?? 0);
    },
    120_000,
  );

  // Modul: the real regression guard for obligation 1.
  //
  // The test above CANNOT catch a client that forgets to echo
  // LogicEpochCounter: a freshly provisioned account sits at epoch 0 for its
  // whole first session, and 0 is exactly what a forgetful client would send.
  // That is precisely why the bug survived initial testing. Only a played-in
  // account - the dev fixture, at epoch 23 - fails fast enough to be useful,
  // and it fails on the FIRST command.
  // Also the reliable home for the loot assertion: this account is level 40
  // with full gear, so it kills region-1 monsters continuously and produces a
  // steady drop stream rather than a 35% coin flip.
  it(
    'survives a played-in account, whose non-zero epoch a forgetful client would fail',
    async () => {
      const { loginWithEmail } = await import('../src/lib/net/auth');
      const { GameConnection } = await import('../src/lib/net/connection');
      const { CommandType } = await import('../src/lib/net/protocol.generated');

      let token: string;
      try {
        token = (await loginWithEmail('dev@folkidle.local', 'FolkIdleDev123!')).token;
      } catch {
        // The fixture is opt-in (--seed-dev). Without it there is nothing to
        // assert, and inventing a played-in account here would be worse.
        console.warn('dev fixture not present - skipping the non-zero-epoch guard');
        return;
      }

      const connection = new GameConnection();
      let terminated = '';
      let sawNonZeroEpoch = false;
      let updates = 0;
      let armed = false;
      let lootDrops = 0;
      let quarantined = false;

      await new Promise<void>((resolve) => {
        const finish = setTimeout(() => resolve(), 20_000);
        connection.connect(token, {
          onStatus: (status) => {
            if (status.phase === 'reconnecting') {
              terminated = status.detail;
              clearTimeout(finish);
              resolve();
            }
          },
          onStateUpdate: (packet) => {
            updates++;
            if (packet.LogicEpochCounter > 0) sawNonZeroEpoch = true;
            if (packet.Quarantine_Active !== 0) quarantined = true;
            if (!armed) {
              armed = true;
              connection.send({ Command: CommandType.ChangeActivity, TargetId: 91 });
            }
          },
          onLootDrop: () => {
            lootDrops++;
          },
        });
      });

      connection.disconnect();

      expect(sawNonZeroEpoch).toBe(true);
      expect(terminated).toBe('');
      expect(updates).toBeGreaterThan(5);

      // A quarantined account is fed spoofed data and drops nothing, so the
      // loot assertion below would fail for a reason that has nothing to do
      // with the client. Diagnosed explicitly, because it took a while to work
      // out the first time: any run of a client that does NOT answer the
      // anti-cheat challenge leaves this flag set PERSISTENTLY, and it must be
      // cleared in the database before the fixture is usable again.
      expect(
        quarantined,
        'The dev fixture is quarantined, so it is fed spoofed data and drops ' +
          'nothing. Some client that never answered an anti-cheat challenge ' +
          'was run against it. Lift it with the real tool - ' +
          '"dotnet FolkIdle.Server.dll --lift-quarantine 1" - which also ' +
          'unfreezes the market listings the shadow ban froze. Do NOT just ' +
          'UPDATE PlayerRecords: that returns the account with its economy ' +
          'still locked.',
      ).toBe(false);

      // ResponseLootDrop is one of the four packet types an earlier draft of
      // the port plan would have omitted from JSON mode entirely, so it is
      // asserted end to end rather than assumed.
      expect(lootDrops).toBeGreaterThan(0);
    },
    60_000,
  );
});

---
name: add-command
description: Add or change a client-to-server command opcode, or add a field to ClientCommandPacket / StateUpdatePacket, in FolkIdle. Use whenever a new player action needs to reach the server or new state needs to reach the client - the wire is generated and hand-editing it breaks startup.
---

# Adding a command or a packet field

The wire format exists in exactly one place: the server's structs. The client's
`protocol.generated.ts` is **generated** from the server's own `--dump-protocol`
output. Never hand-write or hand-patch it.

## Adding a command

1. **Declare the opcode.** `server/FolkIdle.Server/Network/ClientCommandPacket.cs`,
   the `CommandType : byte` enum. Take the next free number; do not reuse a
   retired one. Write a `// Modul:` comment saying what the command carries and
   what the server decides for itself — the existing entries do (see
   `RecruitVillager = 70`, `AssignCharacterSlot = 75`).

2. **Handle it.** The dispatch switch lives in
   `server/FolkIdle.Server/Domain/Combat/SimulationEngine.cs`. The handler runs
   on the tick thread: **it must not touch the database.** DB work goes to
   `Task.Run` and reports back through one of the `ConcurrentQueue<T>` members
   on `PlayerSessionRegistry`, which the tick drains next frame. This queue-drain
   pattern is the backbone of nearly every engine here.

3. **Validate server-side.** The client is not trusted with prices, costs,
   cooldowns or eligibility. If a command carries a number the player could
   have chosen, the server re-derives or re-checks it.

4. **Regenerate the wire.**
   ```powershell
   cd client_web; npm run generate:protocol
   ```
   Requires the .NET SDK — it runs the server with `--dump-protocol`. The output
   is committed so a fresh checkout builds without .NET. `--check` re-generates
   and diffs instead of writing; that is what CI runs.

5. **Send it.** `client_web/src/lib/net/commands.ts`:
   ```ts
   connection.send({ Command: CommandType.YourCommand, /* fields */ });
   ```

6. **Wire the UI**, and add a check to `client_web/scripts/exercise.mjs`. An
   opcode no screen reaches is a known failure mode here — an audit once found
   five of them.

## Adding a FIELD to a packet — read this first

`NetworkPacketLayoutGuard` pins both packet sizes as constants and **throws on
startup if the struct disagrees**. Adding a field means updating the constant
in the same commit:

- `ExpectedClientCommandSize` (currently 359)
- `ExpectedStateUpdateSize`

The client's copy of the size comes from `protocol.generated.ts`, so
regenerating handles that half — but only if you regenerate. The two guards
drifted apart once and the client threw on every startup.

**`StateUpdatePacket` has a 700-byte structural ceiling** that the tests pin,
and as of the last measurement roughly **one byte of headroom**. The next
addition must shrink something else or move the ceiling deliberately. Historic
additions are documented byte-by-byte in the guard's own comments; add yours to
that record.

**`TickStatePayload` is NOT the wire packet.** It is the in-memory per-player
tick state. Caching something onto it costs nothing on the network and needs no
protocol regeneration — that is often the right answer instead of a new field.

## Verify

```powershell
dotnet build server/FolkIdle.Server/FolkIdle.Server.csproj   # stop the server first
cd client_web; npm run check && npm run exercise
```

A packet change that builds and starts is not proven. Only `exercise` shows the
command reaching the server and the world changing.

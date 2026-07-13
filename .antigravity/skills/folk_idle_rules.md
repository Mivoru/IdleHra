# FOLK-IDLE DEVELOPMENT PROTOCOLS AND CODE-GENERATION GUARDRAILS

## 1. System Role and Context Boundary
You are a Senior Systems and Backend Engineer specialized in high-concurrency game architectures, .NET Core optimizations, and distributed data pipelines. All code generated must adhere to production-grade security, optimization, and structural consistency standards.

## 2. Project Topography
The repository is strictly divided into two decoupled domains:
- `/server`: A headless .NET Core console application containing the authoritative game engine, database layers, and tick processing simulation loops.
- `/client`: A Unity 2D engine runtime acting purely as a dumb visual proxy for rendering and UI state representation.

## 3. Server Core Constraints (The Authoritative State)
- **Zero Allocation Memory Model:** Inside the 10 Hz core simulation loop, memory allocations to the managed heap are strictly prohibited. Never utilize LINQ queries, lambda expressions causing closures, or runtime class instantiations. Use unmanaged memory structures, structs, and pass parameters strictly by reference (in, out, ref).
- **Pessimistic Database Isolation:** All modifications to gold balances, market trades, items, or forge actions must execute inside explicit PostgreSQL transactions running under the Serializable isolation level. Append explicit FOR UPDATE row locks to target entity selection queries to prevent concurrent race conditions or duplication exploits.
- **Continuous Logging and Archiving:** Market transactions and ledger movements must be written to historical telemetry archives continuously on execution. Do not use batch-processing or single-block mass insertions during seasonal rollover events to protect the database from WAL exhaustion.

## 4. Economy and Forge Rules (Durability-Free Execution)
- **Durability Elimination:** Equipment items possess no durability or repair parameters. 
- **Deflationary Forge Mechanics:** Item sinks are driven entirely by the Forge via Item Fusion. Upgrading an item from Tier T to T+1 requires 1 primary item and 2 sacrificial items matching the base item ID and quality tier.
- **Fusion Failures:** Tier 2 fusion failure locks a random affix slot permanently (is_affix_locked = true) inside the JSONB payload instead of breaking the item. Tier 3 and Tier 4 failures completely vaporize all 3 input item rows, returning a minor percentage of base materials.
- **Tax and Sink Handling:** During the MVP phase, all marketplace and guild transaction fees are directed straight to the global system sink and burned immediately to suppress hyperinflation.

## 5. Client Core Constraints (The Dumb Proxy)
- **Visualization Only:** The Unity client has zero authority over player state, timers, inventories, or combat processing. It only visualizes data packets transmitted via WebSockets from the server.
- **Predictive LERP Engine:** Use visual interpolation and dead reckoning to smoothly display progress bars and tick states locally. If a state delta exceeds the hard error threshold, execute a hard canvas redraw to synchronize with the server's state packet.

## 6. Code Generation Restrictions
- **No Placeholders:** Do not generate code containing truncated structures, incomplete loops, or // TODO tags. Every code block must be complete and ready for deployment.
- **Formatting Hygiene:** Never output textual icons or emojis within generated source files, strings, log messages, or comment lines.
- **Typed Errors:** Return explicitly typed enums for operational outcomes instead of generic boolean evaluation blocks.
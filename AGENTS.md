# Agent instructions

**The project rules live in [`CLAUDE.md`](./CLAUDE.md) at this same path. Read
that file before doing anything in this repository.**

It is not Claude-specific despite the name — it holds the build and test
commands, the local-stack script, the deployment procedure, and the rules that
are load-bearing (stale-DLL builds, generated wire types, snake_case table
overrides, and why `npm run exercise` is the only verification that proves a
gameplay change works).

This file is a pointer rather than a copy on purpose. Two copies of one truth
drifting apart is this codebase's dominant bug class, and a duplicated rule
sheet would be the largest instance of it.

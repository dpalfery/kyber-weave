---
id: todo/zcode-storage-dir-config-override
title: ZCode's global root can be moved by a config file the resolver cannot see
doc-type: todo
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-09-21
status: draft
---

# ZCode's global root can be moved by a config file the resolver cannot see

`SquadGlobalRoots.ResolveGlobalRoot(SquadTarget.ZCode)` reads `ZCODE_STORAGE_DIR` and falls
back to `~/.zcode`, matching `ISquadGlobalRootResolver`'s environment-variable contract.

ZCode resolves the same value from its merged runtime config
(`config.storage.dir`, `bootstrap/src/app/create-app.ts`). The environment variable is only
one of that value's sources: `adapters/src/config/env-config.adapter.ts` maps
`ZCODE_STORAGE_DIR` onto it, but a user config file at `~/.zcode/cli/config.json` can set
`storage.dir` directly, and `services/src/subagents/subagentStorage.ts` reads exactly that.

So an operator who moved their ZCode storage through the config file rather than the
environment would get a `--global` install written to `~/.zcode` while ZCode reads from
somewhere else. Nothing is overwritten and nothing is lost; the deployment is simply invisible
to the harness.

Verified against [`zai-org/ZCode`](https://github.com/zai-org/ZCode) **3.14.0** on 2026-09-21.

## What would fix it

Reading `~/.zcode/cli/config.json` would make `ISquadGlobalRootResolver` file-aware for one
target, which is a real widening of a deliberately narrow port — every other target resolves
from the environment alone. The cheaper first move is a `squad doctor` check that reports the
mismatch when the file sets `storage.dir` to something other than the resolved root, leaving
the port alone.

Note that ZCode's own beta channel sets `ZCODE_STORAGE_DIR` to `~/.zcode-beta`
(`cli/src/env.ts`), so the environment path is the common one and is already handled.

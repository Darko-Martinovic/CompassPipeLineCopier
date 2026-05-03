# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & run

Single-file .NET 8 console app, one project (`PipelineCopier.csproj`).

```powershell
dotnet build
dotnet run
```

There are no tests, no lint config, and no CI. The solution file `CompassPipelineCopier.sln` exists but `dotnet` commands work fine against the csproj directly.

Note: stale `obj/Debug/net10.0/` artifacts exist from a prior retarget — the project is currently `net8.0`. Ignore those folders; do not "fix" them by changing `TargetFramework`.

## What this tool does

It runs three sequential phases against a hardcoded source/target configuration and prints progress to stdout. There is no CLI, no config file, and no DI — everything is top-level statements in `Program.cs`.

1. **Copy Compass saved pipelines** — reads JSON files from `%APPDATA%\MongoDB Compass\SavedPipelines\`, finds those whose `namespace` matches the source, and writes new copies for each target namespace. Each copy gets a fresh GUID `id`, the rewritten `namespace`, an updated `lastModified` timestamp, and any `_id` field stripped (Compass rejects pipelines that have one). **Before writing, it deletes existing files in that folder whose `namespace` matches a target** — this is intentional cleanup of prior runs, not a bug.
2. **Copy MongoDB indexes** — connects to MongoDB, lists indexes on the source collection, and recreates each on every target collection. Skips `_id_`. Preserves `unique`, `sparse`, `expireAfterSeconds`, `partialFilterExpression`. "Already exists" errors are swallowed and counted as skipped.
3. **Index usage report** — runs `$indexStats` on the source and prints per-index op counts. Read-only.

## Configuration is hardcoded

The source namespace, target namespaces, and Mongo connection string are top-of-file `var`s in `Program.cs` (lines 7–9). Editing them is the supported way to change behavior — there is no args parsing or env-var fallback. If asked to add configurability, propose the approach before refactoring; the current shape is deliberately minimal.

Namespaces are split on `.` into `database.collection` (see `sourceParts`/`targetParts`). A namespace with a dotted collection name would break this — keep that in mind if changing the format.

## Operational gotchas

- **Compass must be restarted** to see newly written pipeline files (the final printed line says this).
- **Phase 1 mutates the user's Compass profile directory.** It deletes files under `%APPDATA%\MongoDB Compass\SavedPipelines\` matching target namespaces. Do not run experimentally against unfamiliar target namespaces — you will delete the user's existing pipelines for those namespaces.
- **Phase 2 writes to MongoDB** at `localhost:27017`. Index creation against a populated collection can be expensive; this tool does not pass `background` or use `createIndexes` batching.
- **Phase 3 requires `$indexStats` privileges** and silently degrades to a printed error if the connection fails — phases 1 and 2 are independent and will already have run.

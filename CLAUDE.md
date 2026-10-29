# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` explains what the tool is for, its options and the rules it follows. This file adds what matters when changing it.

## Build, run, test

.NET 10 console app (`src/PipelineCopier`) with an xunit v3 test project (`tests/PipelineCopier.Tests`), in `CompassPipelineCopier.slnx`. `global.json` pins the SDK band and opts `dotnet test` into Microsoft.Testing.Platform.

```powershell
dotnet build CompassPipelineCopier.slnx
dotnet test --solution CompassPipelineCopier.slnx
dotnet run --project src/PipelineCopier -- --dry-run
```

`TreatWarningsAsErrors` is on in both projects, which includes NuGet's vulnerability audit. The tests start `mongo:8.0` with Testcontainers.MongoDb, one container for the whole assembly (`MongoFixture`, an assembly fixture); each test uses databases of its own (`MongoFixture.NewDatabase`) and a temporary SavedPipelines folder (`CompassFolderFixture`). On this machine Testcontainers runs on Podman and needs `TESTCONTAINERS_RYUK_DISABLED=true`. CI (`.github/workflows/build.yml`) runs on ubuntu-22.04, because MongoDB 8 does not start on the newer kernel of ubuntu-latest.

## Code

- `App.cs`: the System.CommandLine root command and `Run`, which runs the three steps and writes the report to a `TextWriter` (tests pass a `StringWriter`). Exit codes: 0, 1 (`Failed`), 2 (`InvalidArguments`).
- `SavedPipelines.cs`: the Compass step, pure file work, with a `TimeProvider` for `lastModified`.
- `IndexCopier.cs`: creates each index with a raw `createIndexes` command from the source's `listIndexes` document minus `v` and `ns`, so every option is copied without mapping it. Don't go back to `CreateIndexOptions`, which dropped collation, text weights and others. Errors 85 and 86 are conflicts, anything else from the server is a failure.
- `IndexUsage.cs`: `$indexStats`.
- `MongoNamespace.cs`: `database.collection`, split at the first dot (collection names may contain dots).

## Rules the tool keeps (tests cover each)

- Saved pipelines are matched by name: a target pipeline with a source pipeline's name is replaced, others are kept unless `--mirror`.
- Nothing is deleted when the source has no saved pipelines, or when a file isn't a saved pipeline.
- Copies are written (to `<id>.json.partial`, then moved) before anything is deleted.
- `--dry-run` changes nothing, in the folder or in MongoDB.
- The connection string is never printed (it can hold a password); errors name the servers only, and `--connection` has no default value so `--help` can't show `MONGODB_URI`.

## Operational gotchas

- The real SavedPipelines folder is the user's Compass profile. Don't run the tool against it without `--dry-run` or `--compass-folder` pointing somewhere else.
- The defaults (`ThirdApi` to `PreProd` and `Staging`, collection `Pump`) are the user's own working setup; keep them.
- Compass has to be restarted to see new pipeline files.

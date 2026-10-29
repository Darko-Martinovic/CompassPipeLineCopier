# Compass Pipeline Copier

A command-line tool that copies the aggregation pipelines MongoDB Compass saved for one collection to other collections, together with that collection's indexes.

## The problem

It is common to keep the same collection in several databases: one per environment (development, pre-production, staging) or one per data load or release. The data has the same shape everywhere, so the queries you need are the same everywhere too.

MongoDB Compass does not see it that way. A saved aggregation pipeline belongs to exactly one namespace (`database.collection`). Twenty pipelines built and saved against `ThirdApi.Pump` do not exist for `PreProd.Pump` or `Staging.Pump`. Compass has no "copy to another collection" action, so the only way to get them there is to open each pipeline, copy its text, switch to the other collection, paste it, and save it again under the same name. Then do the same for the next collection. And again each time a pipeline changes.

Indexes have the same problem. A collection loaded into a new database starts with nothing but the `_id_` index, so pipelines that ran in milliseconds on the source scan the whole collection on the copy. Recreating each index by hand, with the right unique, TTL, partial filter, collation or text weights, is slow and easy to get wrong.

This tool does both in one run, and can be run again whenever the source changes.

## What it does

A run has three steps. Each can be left out with `--skip`.

### 1. Saved pipelines

Compass stores each saved pipeline as a JSON file, named after the pipeline's id, in its `SavedPipelines` folder:

| System  | Folder |
|---------|--------|
| Windows | `%APPDATA%\MongoDB Compass\SavedPipelines` |
| macOS   | `~/Library/Application Support/MongoDB Compass/SavedPipelines` |
| Linux   | `$XDG_CONFIG_HOME/MongoDB Compass/SavedPipelines` (usually `~/.config/...`) |

The tool reads every file in that folder, takes the pipelines saved for the source namespace, and writes a copy of each one for every target namespace. A copy has a new id, the target's namespace and the current time as its last-modified date; everything else (the pipeline text with its comments, the collation, the preview settings) is kept as it was. Any `_id` field is removed, because Compass refuses to load a saved pipeline that has one.

The rules that keep this safe to run again:

- **Pipelines are matched by name.** A target pipeline with the same name as a source pipeline is replaced by the new copy. Running the tool twice leaves one copy of each pipeline, not two.
- **Pipelines that exist only on a target are kept.** If you built a pipeline on `PreProd.Pump` that the source does not have, it stays. Use `--mirror` to delete those instead, so that each target ends up with exactly the source's pipelines.
- **A source with no saved pipelines changes nothing.** A typo in the source name, or the wrong folder, never empties a target.
- **Copies are written before anything is deleted,** and each file is written under a temporary name first. If a copy can't be written, the copies already written for that target are taken back and nothing is deleted. A run stopped with Ctrl+C stops between targets, and the temporary files of a run that was killed are removed by the next one.
- **Files that are not saved pipelines are left alone** and listed in the output: files that aren't JSON, have no namespace, or have the same property twice.
- **Source pipelines without a name are not copied,** since they can't be matched on a target. Compass always names a pipeline it saves, so this only happens to files edited by hand. If two source pipelines share a name, the output says so and each target gets both.

Compass reads this folder when it starts, so restart it to see the copies.

### 2. Indexes

The tool lists the source collection's indexes and creates each of them on every target collection, except `_id_` and a clustered collection's clustered index, which come with the collection.

A target collection that does not exist is created first (with its database), with the options the source was created with: a time-series collection becomes a time-series collection with the same time and meta fields, and a clustered, capped or validated collection, or one with a default collation, is created the same way. A target that already exists is used as it is.

Each index is created from the definition `listIndexes` reports for it, so every option it was made with is copied: unique, sparse, TTL, partial filter expressions, collation, text index weights and languages, wildcard projections, hidden, 2d and 2dsphere settings. Each one ends up as:

| Outcome | Meaning |
|---------|---------|
| created | The index did not exist on the target and was built. |
| already exists | An identical index is there already. Nothing changed. |
| conflict | The target has a different index with the same name, or the same keys under a different name. It is left as it is and the server's explanation is printed. |
| failed | The server refused the index for another reason, for example a unique index over duplicate values on the target. The run ends with exit code 1. |

A dry run works out the same outcomes from the indexes the target already has, without building anything. A target the tool can't work on (a view, a database the user can't write to, or a name that differs from an existing database's only in case) is reported as failed, and the other targets are still copied. A source that is a view has no indexes to copy, and the step says so.

### 3. Index usage report

Reads `$indexStats` on the source collection and prints how many operations used each index since the server started counting. On a sharded cluster the counts of every shard are added up. An index with no operations is a candidate to drop. The report ends with the profiler commands for finding queries that need an index the collection does not have yet.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A MongoDB server reachable from where the tool runs (the tests use MongoDB 8.0)
- MongoDB Compass installed for the current user, for the saved-pipelines step
- A MongoDB user with `read` on the source database, `readWrite` on the target databases, and the `indexStats` action on the source for the report (the built-in `clusterMonitor` role has it)

## Usage

```
dotnet run --project src/PipelineCopier -- [source] [targets...] [options]
```

`source` and each target are either a database name, for the collection named with `--collection`, or a full `database.collection` namespace. With no arguments, the source is `ThirdApi` and the targets are `PreProd` and `Staging`, all in the collection `Pump`.

| Option | Description |
|--------|-------------|
| `-c`, `--collection <name>` | The collection for arguments that name only a database. Default: `Pump`. |
| `--connection <uri>` | The MongoDB connection string. Default: the `MONGODB_URI` environment variable, or `mongodb://localhost:27017`. |
| `--compass-folder <path>` | Compass's `SavedPipelines` folder. Default: the folder Compass uses on this system (see above). |
| `--mirror` | Also delete each target's saved pipelines whose names the source does not have. |
| `--dry-run` | Show what would be copied, replaced, created and deleted, and change nothing. |
| `--skip <steps>` | Leave out one or more steps: `pipelines`, `indexes`, `report`. |
| `-h`, `--help` | Show the options. |

The connection string is never printed: when the server cannot be reached, the output names only its host and port.

### Examples

See what a run would change, without changing anything:

```
dotnet run --project src/PipelineCopier -- ThirdApi PreProd Staging --dry-run
```

Copy the pipelines and indexes of `ThirdApi.Pump` to `PreProd.Pump` and `Staging.Pump`:

```
dotnet run --project src/PipelineCopier -- ThirdApi PreProd Staging
```

Use another collection, and make the target's saved pipelines exactly the source's:

```
dotnet run --project src/PipelineCopier -- ThirdApi PreProd --collection Tos --mirror
```

Copy between collections with different names, pipelines only:

```
dotnet run --project src/PipelineCopier -- Shop.orders.2024 Archive.orders.2024 --skip indexes report
```

Copy only the indexes, to a server that needs a password:

```
MONGODB_URI="mongodb://admin:secret@db.example.com:27017/?authSource=admin" \
  dotnet run --project src/PipelineCopier -- ThirdApi PreProd --skip pipelines
```

### Output

```
MongoDB Compass Pipeline & Index Copier
=======================================
Source:  ThirdApi.Pump
Targets: PreProd.Pump, Staging.Pump

Saved pipelines (Compass)
-------------------------
Folder: C:\Users\me\AppData\Roaming\MongoDB Compass\SavedPipelines
Pipelines saved for ThirdApi.Pump: 3

  → PreProd.Pump:
      ✓ Bulk items
      ✓ Item prices
      ✓ Items without GTIN
      ○ Additional info (not in the source, kept)
    Copied: 3, replacing: 3, deleted: 0, kept: 1
...
```

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Everything asked for was done. Index conflicts are reported but do not fail the run. |
| 1 | Something failed: a pipeline file could not be written or deleted, an index could not be created, or MongoDB could not be reached. |
| 2 | The arguments are wrong: a target that is also the source, a name MongoDB would refuse (an empty name, a database name with a space or one of `/\."$*<>:|?`, a collection name with `$`, or one of MongoDB's own `system.` collections), or a connection string given where a database belongs. |

## Things to know

- **Restart Compass** after a run. It reads the `SavedPipelines` folder when it starts, so a running Compass does not show the copies.
- **The pipeline text is copied as it is.** Stages that name a collection, such as `$lookup`, are relative to the current database and work on the target. A `$merge` or `$out` that names a database explicitly still points at the source's database.
- **Names are compared exactly.** `Ingredients` and `Ingredients ` (with a trailing space) are two different pipelines.
- **Building indexes on a large, populated target takes time and server resources.** Try `--dry-run` first, and run against production-sized data outside busy hours.
- **On a sharded cluster** the tool works through `mongos`. An index counts as already there only when no shard built it.

## Building and testing

```
dotnet build CompassPipelineCopier.slnx
dotnet test --solution CompassPipelineCopier.slnx
```

The tests start MongoDB 8.0 in a container with [Testcontainers](https://dotnet.testcontainers.org/), so they need Docker or Podman running. With Podman, set `TESTCONTAINERS_RYUK_DISABLED=true`. The saved-pipeline tests use a temporary folder of their own and never touch Compass's real one.

GitHub Actions runs the build and the tests on every push to `main` and every pull request.

## Project layout

```
src/PipelineCopier/
  Program.cs          entry point
  App.cs              command line and the three steps of a run
  SavedPipelines.cs   copying Compass's saved pipelines
  IndexCopier.cs      copying indexes
  IndexUsage.cs       the $indexStats report
  MongoNamespace.cs   database.collection names
  CompassFolder.cs    where Compass keeps saved pipelines on each system
tests/PipelineCopier.Tests/
```

## License

[MIT](LICENSE)

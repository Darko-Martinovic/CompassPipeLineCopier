# Compass Pipeline Copier

A command-line tool that copies the aggregation pipelines MongoDB Compass saved for one collection to other collections, together with that collection's indexes.

A saved pipeline in Compass belongs to one namespace (`database.collection`), and Compass has no way to copy it to another collection. A collection loaded into a new database also starts with only the `_id_` index. This tool copies both in one run, and can be run again whenever the source changes.

## Install

```
dotnet tool install --global CompassPipelineCopier
```

It needs the .NET 10 runtime.

## Use

```
compass-pipeline-copier <source> <targets...> [options]
```

`source` and each target are a database name, for the collection named with `--collection`, or a full `database.collection` namespace.

See what a run would change, without changing anything:

```
compass-pipeline-copier ThirdApi PreProd Staging --collection Pump --dry-run
```

Copy the saved pipelines and indexes of `ThirdApi.Pump` to `PreProd.Pump` and `Staging.Pump`:

```
compass-pipeline-copier ThirdApi PreProd Staging --collection Pump
```

| Option | Description |
|--------|-------------|
| `-c`, `--collection <name>` | The collection for arguments that name only a database. Default: `Pump`. |
| `--connection <uri>` | The MongoDB connection string. Default: the `MONGODB_URI` environment variable, or `mongodb://localhost:27017`. |
| `--compass-folder <path>` | Compass's `SavedPipelines` folder. Default: the folder Compass uses on this system. |
| `--mirror` | Also delete each target's saved pipelines whose names the source does not have. |
| `--dry-run` | Show what would be copied, replaced, created and deleted, and change nothing. |
| `--skip <steps>` | Leave out one or more steps: `pipelines`, `indexes`, `report`. |

What it keeps safe:

- Saved pipelines are matched by name, so running it twice leaves one copy of each. A target's own pipelines are kept unless you pass `--mirror`.
- A source with no saved pipelines changes nothing, and copies are written before anything is deleted.
- Every index option is copied: unique, sparse, TTL, partial filters, collation, text weights, wildcard projections, hidden. An index that differs from one already on the target is reported as a conflict and left alone.
- The connection string is never printed.

Restart Compass after a run to see the copies.

The full documentation, with every rule and exit code, is on [GitHub](https://github.com/Darko-Martinovic/CompassPipeLineCopier#readme).

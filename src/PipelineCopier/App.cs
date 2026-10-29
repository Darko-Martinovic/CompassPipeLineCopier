using System.CommandLine;
using System.Text.Json;
using MongoDB.Driver;

namespace PipelineCopier;

public enum Step
{
    Pipelines,
    Indexes,
    Report,
}

/// <summary>What one run is asked to do, from the command line.</summary>
public sealed record RunOptions(
    MongoNamespace Source,
    IReadOnlyList<MongoNamespace> Targets,
    string ConnectionString,
    string SavedPipelinesFolder,
    bool Mirror,
    bool DryRun,
    IReadOnlySet<Step> Skip);

public static class App
{
    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int InvalidArguments = 2;

    private const string DefaultConnectionString = "mongodb://localhost:27017";

    public static Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var source = new Argument<string>("source")
        {
            Description = "The database to copy from, or a full database.collection namespace",
            DefaultValueFactory = _ => "ThirdApi",
        };
        var targets = new Argument<string[]>("targets")
        {
            Description = "The databases (or database.collection namespaces) to copy to",
            Arity = ArgumentArity.ZeroOrMore,
            DefaultValueFactory = _ => ["PreProd", "Staging"],
        };
        var collection = new Option<string>("--collection", "-c")
        {
            Description = "The collection, for arguments that name only a database",
            DefaultValueFactory = _ => "Pump",
        };
        // Not given a default value, so that --help never prints a connection string with a password in it
        var connection = new Option<string?>("--connection")
        {
            Description = $"The MongoDB connection string [default: MONGODB_URI, or {DefaultConnectionString}]",
        };
        var compassFolder = new Option<string?>("--compass-folder")
        {
            Description = "Compass's SavedPipelines folder [default: the folder Compass uses on this system]",
        };
        var mirror = new Option<bool>("--mirror")
        {
            Description = "Also delete the saved pipelines of a target whose names the source doesn't have",
        };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would be copied, replaced and deleted, and change nothing",
        };
        var skip = new Option<Step[]>("--skip")
        {
            Description = "Steps to leave out: pipelines, indexes, report",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new RootCommand(
            "Copies the aggregation pipelines MongoDB Compass saved for one collection, and the collection's indexes, to other collections.")
        {
            source, targets, collection, connection, compassFolder, mirror, dryRun, skip,
        };

        command.SetAction((result, token) =>
        {
            var collectionName = result.GetValue(collection)!;
            MongoNamespace from;
            List<MongoNamespace> to;
            try
            {
                from = MongoNamespace.FromArgument(result.GetValue(source)!, collectionName);
                to = result.GetValue(targets)!.Select(t => MongoNamespace.FromArgument(t, collectionName)).Distinct().ToList();
            }
            catch (FormatException e)
            {
                output.WriteLine(e.Message);
                return Task.FromResult(InvalidArguments);
            }

            if (to.Contains(from))
            {
                output.WriteLine($"{from} is both the source and a target.");
                return Task.FromResult(InvalidArguments);
            }

            var options = new RunOptions(
                from,
                to,
                result.GetValue(connection) ?? Environment.GetEnvironmentVariable("MONGODB_URI") ?? DefaultConnectionString,
                result.GetValue(compassFolder) ?? CompassFolder.SavedPipelines(),
                result.GetValue(mirror),
                result.GetValue(dryRun),
                (result.GetValue(skip) ?? []).ToHashSet());

            return new Run(options, output, TimeProvider.System).ExecuteAsync(token);
        });

        return command.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, cancellationToken);
    }
}

/// <summary>One run of the three steps, writing what it does to the output.</summary>
public sealed class Run(RunOptions options, TextWriter output, TimeProvider time)
{
    private bool _failed;

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        output.WriteLine("MongoDB Compass Pipeline & Index Copier");
        output.WriteLine("=======================================");
        output.WriteLine($"Source:  {options.Source}");
        output.WriteLine($"Targets: {string.Join(", ", options.Targets)}");
        if (options.DryRun)
        {
            output.WriteLine("Dry run: nothing will be changed");
        }

        output.WriteLine();

        if (!options.Skip.Contains(Step.Pipelines))
        {
            CopyPipelines(cancellationToken);
        }

        if (!options.Skip.Contains(Step.Indexes) || !options.Skip.Contains(Step.Report))
        {
            await UseMongoAsync(cancellationToken);
        }

        output.WriteLine("=======================================");
        output.WriteLine(_failed ? "Finished with errors." : "Done.");
        if (!options.Skip.Contains(Step.Pipelines) && !options.DryRun)
        {
            output.WriteLine("Restart MongoDB Compass to see the copied pipelines.");
        }

        return _failed ? App.Failed : App.Succeeded;
    }

    private void CopyPipelines(CancellationToken cancellationToken)
    {
        Heading("Saved pipelines (Compass)");
        var pipelines = new SavedPipelines(options.SavedPipelinesFolder, time);
        output.WriteLine($"Folder: {pipelines.Folder}");

        if (!pipelines.Exists)
        {
            output.WriteLine("The folder doesn't exist. Is Compass installed for this user? Use --compass-folder to name it.");
            output.WriteLine();
            return;
        }

        PipelineCopyReport report;
        try
        {
            report = pipelines.Copy(options.Source, options.Targets, options.Mirror, options.DryRun, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failed = true;
            output.WriteLine($"Could not read the folder: {e.Message}");
            output.WriteLine();
            return;
        }

        foreach (var file in report.Unreadable)
        {
            output.WriteLine($"  Left alone, not a saved pipeline: {file}");
        }

        if (report.Unnamed > 0)
        {
            output.WriteLine($"  Not copied: {report.Unnamed} pipeline(s) of {options.Source} without a name");
        }

        foreach (var name in report.DuplicateNames ?? [])
        {
            output.WriteLine($"  More than one pipeline of {options.Source} is named {name}; each target gets all of them");
        }

        if (report.SourceCount == 0)
        {
            output.WriteLine($"No saved pipelines for {options.Source}, so no target was changed.");
            output.WriteLine();
            return;
        }

        output.WriteLine($"Pipelines saved for {options.Source}: {report.SourceCount}");
        output.WriteLine();

        foreach (var target in report.Targets)
        {
            output.WriteLine($"  → {target.Target}:");
            foreach (var name in target.Copied)
            {
                output.WriteLine($"      ✓ {name}");
            }

            foreach (var name in target.Removed)
            {
                output.WriteLine($"      - {name} (not in the source, {(options.DryRun ? "would be deleted" : "deleted")})");
            }

            foreach (var name in target.Kept)
            {
                output.WriteLine($"      ○ {name} (not in the source, kept)");
            }

            if (target.Error is not null)
            {
                _failed = true;
                output.WriteLine($"      ✗ Stopped: {target.Error}");
            }

            var copied = options.DryRun ? "Would copy" : "Copied";
            output.WriteLine($"    {copied}: {target.Copied.Count}, replacing: {target.Replaced}, deleted: {target.Removed.Count}, kept: {target.Kept.Count}");
            output.WriteLine();
        }
    }

    private async Task UseMongoAsync(CancellationToken cancellationToken)
    {
        MongoClientSettings settings;
        try
        {
            settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        }
        catch (Exception)
        {
            // The driver's messages can repeat the connection string, password and all
            _failed = true;
            output.WriteLine("The MongoDB connection string is not valid.");
            output.WriteLine();
            return;
        }

        // A server that isn't there should say so quickly, not after the driver's 30 seconds
        var query = options.ConnectionString.Split('?', 2) is [_, var rest] ? rest : "";
        if (!query.Contains("serverSelectionTimeoutMS", StringComparison.OrdinalIgnoreCase))
        {
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        }

        using var client = new MongoClient(settings);
        try
        {
            if (!options.Skip.Contains(Step.Indexes))
            {
                await CopyIndexesAsync(client, cancellationToken);
            }

            if (!options.Skip.Contains(Step.Report))
            {
                await ReportUsageAsync(client, cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            _failed = true;
            // The servers only, never the connection string, which can hold a password
            output.WriteLine($"Could not reach MongoDB at {string.Join(", ", settings.Servers)}. Is it running?");
            output.WriteLine();
        }
        catch (MongoException e)
        {
            _failed = true;
            output.WriteLine($"MongoDB refused: {e.Message}");
            output.WriteLine();
        }
    }

    private async Task CopyIndexesAsync(IMongoClient client, CancellationToken cancellationToken)
    {
        Heading("Indexes (MongoDB)");
        var copier = new IndexCopier(client);
        SourceCollection? sourceCollection;
        try
        {
            sourceCollection = await copier.ReadSourceAsync(options.Source, cancellationToken);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"{e.Message} No indexes were copied.");
            output.WriteLine();
            return;
        }

        if (sourceCollection is null)
        {
            output.WriteLine($"The collection {options.Source} doesn't exist, so no indexes were copied.");
            output.WriteLine();
            return;
        }

        output.WriteLine($"Indexes on {options.Source}: {sourceCollection.Indexes.Count}");
        foreach (var index in sourceCollection.Indexes)
        {
            output.WriteLine($"  - {index["name"].AsString}: {index["key"]}");
        }

        output.WriteLine();

        foreach (var target in options.Targets)
        {
            output.WriteLine($"  → {target}:");
            var result = await copier.CopyAsync(sourceCollection, target, options.DryRun, cancellationToken);
            if (result.Error is not null)
            {
                _failed = true;
                output.WriteLine($"      ✗ {result.Error}");
                output.WriteLine();
                continue;
            }

            if (result.CollectionCreated)
            {
                output.WriteLine($"      ({(options.DryRun ? "would create" : "created")} the collection {target}, with the source's options)");
            }

            foreach (var index in result.Indexes)
            {
                output.WriteLine(index.Outcome switch
                {
                    IndexOutcome.Created => $"      ✓ {index.Name}",
                    IndexOutcome.WouldCreate => $"      ✓ {index.Name} (would be created)",
                    IndexOutcome.AlreadyExists => $"      ○ {index.Name} (already exists)",
                    IndexOutcome.Conflict => $"      ! {index.Name}: {index.Message}",
                    _ => $"      ✗ {index.Name}: {index.Message}",
                });
            }

            if (result.Indexes.Any(i => i.Outcome == IndexOutcome.Failed))
            {
                _failed = true;
            }

            var created = result.Indexes.Count(i => i.Outcome is IndexOutcome.Created or IndexOutcome.WouldCreate);
            var existing = result.Indexes.Count(i => i.Outcome == IndexOutcome.AlreadyExists);
            var conflicts = result.Indexes.Count(i => i.Outcome == IndexOutcome.Conflict);
            var failed = result.Indexes.Count(i => i.Outcome == IndexOutcome.Failed);
            output.WriteLine($"    {(options.DryRun ? "Would create" : "Created")}: {created}, already there: {existing}, conflicts: {conflicts}, failed: {failed}");
            output.WriteLine();
        }
    }

    private async Task ReportUsageAsync(IMongoClient client, CancellationToken cancellationToken)
    {
        Heading($"Index usage on {options.Source}");
        try
        {
            foreach (var use in await IndexUsage.ReadAsync(client, options.Source, cancellationToken))
            {
                var since = use.Since is { } date ? $" since {date:yyyy-MM-dd HH:mm} UTC" : "";
                output.WriteLine($"  - {use.Name}: {use.Operations} ops{since}");
            }
        }
        catch (MongoCommandException e)
        {
            // Usually a user without the indexStats privilege; the copies above are done either way
            output.WriteLine($"Could not read index usage: {e.ErrorMessage}");
        }

        output.WriteLine();
        output.WriteLine("Indexes with no ops are candidates to drop. To find missing ones, profile slow queries:");
        output.WriteLine("  db.setProfilingLevel(1, { slowms: 100 })");
        output.WriteLine($"  db.system.profile.find({{ ns: {JsonSerializer.Serialize(options.Source.ToString())} }}).sort({{ ts: -1 }}).limit(10)");
        output.WriteLine("and look for planSummary: COLLSCAN.");
        output.WriteLine();
    }

    private void Heading(string text)
    {
        output.WriteLine(text);
        output.WriteLine(new string('-', text.Length));
    }
}

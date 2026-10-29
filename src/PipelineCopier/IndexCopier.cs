using MongoDB.Bson;
using MongoDB.Driver;

namespace PipelineCopier;

public enum IndexOutcome
{
    Created,
    AlreadyExists,
    WouldCreate,
    /// <summary>The target has an index with the same keys or the same name, defined differently.</summary>
    Conflict,
    Failed,
}

public sealed record IndexCopyResult(string Name, IndexOutcome Outcome, string? Message = null);

/// <param name="CollectionCreated">The target collection didn't exist and was created (or, in a dry run, would be).</param>
/// <param name="Error">Why nothing could be done on this target, if so.</param>
public sealed record TargetIndexes(
    MongoNamespace Target, bool CollectionCreated, IReadOnlyList<IndexCopyResult> Indexes, string? Error = null);

/// <summary>A collection's indexes, and the options it was created with.</summary>
/// <param name="Options">What listCollections reports: capped, validator, collation, timeseries, clusteredIndex and the rest.</param>
public sealed record SourceCollection(BsonDocument Options, IReadOnlyList<BsonDocument> Indexes);

/// <summary>
/// Recreates a collection's indexes on other collections, with every option they were defined
/// with: unique, sparse, TTL, partial filters, collation, text weights, wildcard projections,
/// hidden and the rest.
/// </summary>
public sealed class IndexCopier(IMongoClient client)
{
    // Error codes createIndexes answers with when the target already has a different index
    // with the same keys (IndexOptionsConflict) or the same name (IndexKeySpecsConflict)
    private const int IndexOptionsConflict = 85;
    private const int IndexKeySpecsConflict = 86;

    /// <summary>The source collection, or null when there is no such collection.</summary>
    /// <exception cref="InvalidOperationException">The source is a view, which has no indexes of its own.</exception>
    public async Task<SourceCollection?> ReadSourceAsync(MongoNamespace source, CancellationToken cancellationToken)
    {
        var database = client.GetDatabase(source.Database);
        var info = await CollectionInfoAsync(database, source.Collection, cancellationToken);
        if (info is null)
        {
            return null;
        }

        if (IsView(info))
        {
            throw new InvalidOperationException($"{source} is a view, which has no indexes of its own.");
        }

        var cursor = await database.GetCollection<BsonDocument>(source.Collection).Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        return new SourceCollection(info.GetValue("options", new BsonDocument()).AsBsonDocument, indexes);
    }

    public async Task<TargetIndexes> CopyAsync(SourceCollection source, MongoNamespace target, bool dryRun, CancellationToken cancellationToken)
    {
        try
        {
            return await CopyToAsync(source, target, dryRun, cancellationToken);
        }
        catch (MongoCommandException e)
        {
            // A target the user can't write to, or a database name that differs from an existing
            // one only in case: the other targets are still copied
            return new TargetIndexes(target, false, [], e.ErrorMessage);
        }
    }

    private async Task<TargetIndexes> CopyToAsync(SourceCollection source, MongoNamespace target, bool dryRun, CancellationToken cancellationToken)
    {
        var database = client.GetDatabase(target.Database);
        var info = await CollectionInfoAsync(database, target.Collection, cancellationToken);
        if (info is not null && IsView(info))
        {
            return new TargetIndexes(target, false, [], $"{target} is a view, which can't have indexes.");
        }

        if (info is null && !dryRun)
        {
            await CreateCollectionAsync(database, target.Collection, source.Options, cancellationToken);
        }

        var existing = info is null ? [] : await IndexesAsync(database, target.Collection, cancellationToken);
        var results = new List<IndexCopyResult>();

        foreach (var index in source.Indexes.Where(i => !Implicit(i)))
        {
            results.Add(dryRun
                ? Predict(index, existing)
                : await CreateAsync(database, target.Collection, index, cancellationToken));
        }

        return new TargetIndexes(target, info is null, results);
    }

    // Every collection has _id_, and a clustered collection's clustered index is part of the
    // collection: it comes with the options the collection is created with, and createIndexes refuses it
    private static bool Implicit(BsonDocument index) =>
        index["name"].AsString == "_id_" || index.GetValue("clustered", false).ToBoolean();

    private static async Task CreateCollectionAsync(
        IMongoDatabase database, string collection, BsonDocument options, CancellationToken cancellationToken)
    {
        // Created like the source: time-series, clustered, capped, with its validator and default
        // collation. Creating the collection also creates the database
        var command = new BsonDocument("create", collection);
        foreach (var option in options)
        {
            command[option.Name] = option.Name == "clusteredIndex" ? WithoutServerFields(option.Value.AsBsonDocument) : option.Value;
        }

        await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
    }

    private static async Task<IndexCopyResult> CreateAsync(
        IMongoDatabase database, string collection, BsonDocument index, CancellationToken cancellationToken)
    {
        var name = index["name"].AsString;
        var command = new BsonDocument
        {
            { "createIndexes", collection },
            { "indexes", new BsonArray { WithoutServerFields(index) } },
        };

        try
        {
            var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);
            return new IndexCopyResult(name, Unchanged(reply) ? IndexOutcome.AlreadyExists : IndexOutcome.Created);
        }
        catch (MongoCommandException e) when (e.Code is IndexOptionsConflict or IndexKeySpecsConflict)
        {
            return new IndexCopyResult(name, IndexOutcome.Conflict, e.ErrorMessage);
        }
        catch (MongoCommandException e)
        {
            return new IndexCopyResult(name, IndexOutcome.Failed, e.ErrorMessage);
        }
    }

    // An identical index already there is not an error: the count just stays the same. Through
    // mongos the counts are per shard, under raw
    private static bool Unchanged(BsonDocument reply)
    {
        var counts = reply.TryGetValue("raw", out var raw) && raw.IsBsonDocument
            ? raw.AsBsonDocument.Values.Where(v => v.IsBsonDocument).Select(v => v.AsBsonDocument).ToList()
            : [reply];

        return counts.Count > 0 && counts.All(c =>
            c.TryGetValue("numIndexesBefore", out var before)
            && c.TryGetValue("numIndexesAfter", out var after)
            && before.ToInt32() == after.ToInt32());
    }

    // What createIndexes would answer, from what the target has
    private static IndexCopyResult Predict(BsonDocument index, IReadOnlyList<BsonDocument> existing)
    {
        var name = index["name"].AsString;
        var specification = WithoutServerFields(index);

        if (existing.FirstOrDefault(e => e["name"].AsString == name) is { } sameName)
        {
            return WithoutServerFields(sameName) == specification
                ? new IndexCopyResult(name, IndexOutcome.AlreadyExists)
                : new IndexCopyResult(name, IndexOutcome.Conflict, $"The target has a different index named {name}.");
        }

        if (existing.FirstOrDefault(e => e["key"] == index["key"]) is { } sameKeys)
        {
            return new IndexCopyResult(name, IndexOutcome.Conflict, $"The target has an index on the same keys named {sameKeys["name"].AsString}.");
        }

        return new IndexCopyResult(name, IndexOutcome.WouldCreate);
    }

    // The index as listIndexes describes it is what createIndexes takes, without the version and
    // namespace the server adds; the target server picks its own version
    private static BsonDocument WithoutServerFields(BsonDocument index)
    {
        var specification = index.DeepClone().AsBsonDocument;
        specification.Remove("v");
        specification.Remove("ns");
        return specification;
    }

    private static bool IsView(BsonDocument info) => info.GetValue("type", "collection").AsString == "view";

    private static async Task<BsonDocument?> CollectionInfoAsync(IMongoDatabase database, string collection, CancellationToken cancellationToken)
    {
        var options = new ListCollectionsOptions { Filter = new BsonDocument("name", collection) };
        var cursor = await database.ListCollectionsAsync(options, cancellationToken);
        return await cursor.FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<BsonDocument>> IndexesAsync(IMongoDatabase database, string collection, CancellationToken cancellationToken)
    {
        var cursor = await database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(cancellationToken);
        return await cursor.ToListAsync(cancellationToken);
    }
}

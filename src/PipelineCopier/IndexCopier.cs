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

public sealed record TargetIndexes(MongoNamespace Target, bool CollectionCreated, IReadOnlyList<IndexCopyResult> Indexes);

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

    /// <summary>The source collection's indexes, or null when there is no such collection.</summary>
    public async Task<IReadOnlyList<BsonDocument>?> ListAsync(MongoNamespace source, CancellationToken cancellationToken)
    {
        var database = client.GetDatabase(source.Database);
        if (!await CollectionExistsAsync(database, source.Collection, cancellationToken))
        {
            return null;
        }

        var cursor = await database.GetCollection<BsonDocument>(source.Collection).Indexes.ListAsync(cancellationToken);
        return await cursor.ToListAsync(cancellationToken);
    }

    public async Task<TargetIndexes> CopyAsync(
        IReadOnlyList<BsonDocument> sourceIndexes, MongoNamespace target, bool dryRun, CancellationToken cancellationToken)
    {
        var database = client.GetDatabase(target.Database);
        var exists = await CollectionExistsAsync(database, target.Collection, cancellationToken);
        var created = false;

        if (!exists && !dryRun)
        {
            // Creating the collection also creates the database, even when only _id_ is copied
            await database.CreateCollectionAsync(target.Collection, cancellationToken: cancellationToken);
            created = true;
        }

        var existing = exists ? await IndexNamesAsync(database, target.Collection, cancellationToken) : [];
        var results = new List<IndexCopyResult>();

        // Every collection has _id_ already
        foreach (var index in sourceIndexes.Where(i => i["name"].AsString != "_id_"))
        {
            var name = index["name"].AsString;
            results.Add(dryRun
                ? new IndexCopyResult(name, existing.Contains(name) ? IndexOutcome.AlreadyExists : IndexOutcome.WouldCreate)
                : await CreateAsync(database, target.Collection, index, cancellationToken));
        }

        return new TargetIndexes(target, created, results);
    }

    private static async Task<IndexCopyResult> CreateAsync(
        IMongoDatabase database, string collection, BsonDocument index, CancellationToken cancellationToken)
    {
        var name = index["name"].AsString;

        // The index as listIndexes describes it is what createIndexes takes, without the
        // version and namespace the server adds; the target server picks its own version
        var specification = index.DeepClone().AsBsonDocument;
        specification.Remove("v");
        specification.Remove("ns");

        var command = new BsonDocument
        {
            { "createIndexes", collection },
            { "indexes", new BsonArray { specification } },
        };

        try
        {
            var reply = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);

            // An identical index already there is not an error: the count just stays the same
            var unchanged = reply.TryGetValue("numIndexesBefore", out var before)
                && reply.TryGetValue("numIndexesAfter", out var after)
                && before.ToInt32() == after.ToInt32();

            return new IndexCopyResult(name, unchanged ? IndexOutcome.AlreadyExists : IndexOutcome.Created);
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

    private static async Task<bool> CollectionExistsAsync(IMongoDatabase database, string collection, CancellationToken cancellationToken)
    {
        var options = new ListCollectionNamesOptions { Filter = new BsonDocument("name", collection) };
        var names = await database.ListCollectionNamesAsync(options, cancellationToken);
        return await names.AnyAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> IndexNamesAsync(IMongoDatabase database, string collection, CancellationToken cancellationToken)
    {
        var cursor = await database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        return indexes.Select(i => i["name"].AsString).ToHashSet(StringComparer.Ordinal);
    }
}

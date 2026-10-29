using MongoDB.Bson;
using MongoDB.Driver;

namespace PipelineCopier;

/// <summary>How often an index has been used since the server started counting.</summary>
public sealed record IndexUse(string Name, long Operations, DateTime? Since);

public static class IndexUsage
{
    /// <summary>
    /// Reads $indexStats for a collection. The counts are per server and start again when the
    /// server restarts or the index is rebuilt. Needs the indexStats privilege.
    /// </summary>
    public static async Task<IReadOnlyList<IndexUse>> ReadAsync(IMongoClient client, MongoNamespace ns, CancellationToken cancellationToken)
    {
        var collection = client.GetDatabase(ns.Database).GetCollection<BsonDocument>(ns.Collection);
        var pipeline = new BsonDocument[] { new("$indexStats", new BsonDocument()) };
        var cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken);
        var stats = await cursor.ToListAsync(cancellationToken);

        return stats
            .Select(stat =>
            {
                var accesses = stat.GetValue("accesses", new BsonDocument()).AsBsonDocument;
                var since = accesses.GetValue("since", BsonNull.Value);
                return new IndexUse(
                    stat["name"].AsString,
                    accesses.GetValue("ops", 0).ToInt64(),
                    since.IsBsonDateTime ? since.ToUniversalTime() : null);
            })
            .OrderBy(use => use.Name, StringComparer.Ordinal)
            .ToList();
    }
}

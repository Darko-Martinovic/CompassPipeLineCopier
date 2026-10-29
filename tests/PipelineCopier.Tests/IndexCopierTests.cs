using MongoDB.Bson;
using MongoDB.Driver;

namespace PipelineCopier.Tests;

public sealed class IndexCopierTests(MongoFixture mongo)
{
    private readonly MongoNamespace _source = new(MongoFixture.NewDatabase("source"), "Pump");
    private readonly MongoNamespace _target = new(MongoFixture.NewDatabase("target"), "Pump");

    private IMongoCollection<BsonDocument> Collection(MongoNamespace ns) =>
        mongo.Client.GetDatabase(ns.Database).GetCollection<BsonDocument>(ns.Collection);

    private async Task<Dictionary<string, BsonDocument>> IndexesAsync(MongoNamespace ns)
    {
        var cursor = await Collection(ns).Indexes.ListAsync(TestContext.Current.CancellationToken);
        var indexes = await cursor.ToListAsync(TestContext.Current.CancellationToken);
        return indexes.ToDictionary(i => i["name"].AsString);
    }

    private async Task<TargetIndexes> CopyAsync(bool dryRun = false)
    {
        var copier = new IndexCopier(mongo.Client);
        var sourceIndexes = await copier.ListAsync(_source, TestContext.Current.CancellationToken);
        Assert.NotNull(sourceIndexes);
        return await copier.CopyAsync(sourceIndexes, _target, dryRun, TestContext.Current.CancellationToken);
    }

    private Task CreateAsync(string keys, CreateIndexOptions options) =>
        Collection(_source).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(BsonDocument.Parse(keys), options),
            cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Every_option_of_an_index_is_copied()
    {
        // The first version copied unique, sparse, TTL and partial filters only, and read TTL as an int
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true, Collation = new Collation("en", strength: CollationStrength.Secondary) });
        await CreateAsync("{ seenAt: 1 }", new CreateIndexOptions { Name = "ttl", ExpireAfter = TimeSpan.FromHours(1) });
        await CreateAsync("{ code: 1 }", new CreateIndexOptions<BsonDocument> { Name = "partial", PartialFilterExpression = BsonDocument.Parse("{ code: { $exists: true } }") });
        await CreateAsync("{ title: 'text', body: 'text' }", new CreateIndexOptions { Name = "search", Weights = BsonDocument.Parse("{ title: 10 }"), DefaultLanguage = "german" });
        await CreateAsync("{ 'attributes.$**': 1 }", new CreateIndexOptions { Name = "wildcard" });
        await CreateAsync("{ store: 1, at: -1 }", new CreateIndexOptions { Name = "hidden", Hidden = true, Sparse = true });

        var result = await CopyAsync();

        Assert.True(result.CollectionCreated);
        Assert.All(result.Indexes, i => Assert.Equal(IndexOutcome.Created, i.Outcome));
        var source = await IndexesAsync(_source);
        var target = await IndexesAsync(_target);
        Assert.Equal(source.Keys.Order(), target.Keys.Order());
        foreach (var (name, index) in source)
        {
            Assert.Equal(index, target[name]);
        }
    }

    [Fact]
    public async Task Copying_again_finds_the_indexes_there()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true });
        await CopyAsync();

        var result = await CopyAsync();

        Assert.False(result.CollectionCreated);
        Assert.Equal(IndexOutcome.AlreadyExists, Assert.Single(result.Indexes).Outcome);
    }

    [Fact]
    public async Task A_different_index_of_the_same_name_on_the_target_is_a_conflict()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true });
        await Collection(_target).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(BsonDocument.Parse("{ sku: -1 }"), new CreateIndexOptions { Name = "sku" }),
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await CopyAsync();

        var index = Assert.Single(result.Indexes);
        Assert.Equal(IndexOutcome.Conflict, index.Outcome);
        Assert.NotNull(index.Message);
    }

    [Fact]
    public async Task A_dry_run_creates_nothing()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku" });

        var result = await CopyAsync(dryRun: true);

        Assert.False(result.CollectionCreated);
        Assert.Equal(IndexOutcome.WouldCreate, Assert.Single(result.Indexes).Outcome);
        var databases = await (await mongo.Client.ListDatabaseNamesAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_target.Database, databases);
    }

    [Fact]
    public async Task A_source_collection_that_does_not_exist_has_no_indexes_to_copy()
    {
        var indexes = await new IndexCopier(mongo.Client).ListAsync(_source, TestContext.Current.CancellationToken);

        Assert.Null(indexes);
    }

    [Fact]
    public async Task Index_usage_counts_the_queries_that_used_each_index()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku" });
        await Collection(_source).InsertOneAsync(new BsonDocument("sku", "A1"), cancellationToken: TestContext.Current.CancellationToken);
        await Collection(_source).Find(new BsonDocument("sku", "A1"), new FindOptions { Hint = "sku" }).ToListAsync(TestContext.Current.CancellationToken);

        var usage = await IndexUsage.ReadAsync(mongo.Client, _source, TestContext.Current.CancellationToken);

        Assert.Equal(["_id_", "sku"], usage.Select(u => u.Name));
        Assert.Equal(1, usage.Single(u => u.Name == "sku").Operations);
        Assert.All(usage, u => Assert.NotNull(u.Since));
    }
}

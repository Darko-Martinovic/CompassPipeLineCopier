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

    private async Task<TargetIndexes> CopyAsync(bool dryRun = false, MongoNamespace? target = null)
    {
        var copier = new IndexCopier(mongo.Client);
        var source = await copier.ReadSourceAsync(_source, TestContext.Current.CancellationToken);
        Assert.NotNull(source);
        return await copier.CopyAsync(source, target ?? _target, dryRun, TestContext.Current.CancellationToken);
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

        // It says it would create the collection, and doesn't
        Assert.True(result.CollectionCreated);
        Assert.Equal(IndexOutcome.WouldCreate, Assert.Single(result.Indexes).Outcome);
        var databases = await (await mongo.Client.ListDatabaseNamesAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_target.Database, databases);
    }

    [Fact]
    public async Task A_source_collection_that_does_not_exist_has_no_indexes_to_copy()
    {
        var indexes = await new IndexCopier(mongo.Client).ReadSourceAsync(_source, TestContext.Current.CancellationToken);

        Assert.Null(indexes);
    }

    [Fact]
    public async Task A_dry_run_predicts_what_createIndexes_would_answer()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true });
        await CreateAsync("{ store: 1 }", new CreateIndexOptions { Name = "store" });
        await CreateAsync("{ at: 1 }", new CreateIndexOptions { Name = "at" });
        await CreateAsync("{ code: 1 }", new CreateIndexOptions { Name = "code" });
        await CreateOnTargetAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true });
        await CreateOnTargetAsync("{ store: -1 }", new CreateIndexOptions { Name = "store" });
        await CreateOnTargetAsync("{ at: 1 }", new CreateIndexOptions { Name = "byTime" });

        var result = await CopyAsync(dryRun: true);

        var outcomes = result.Indexes.ToDictionary(i => i.Name, i => i.Outcome);
        Assert.Equal(IndexOutcome.AlreadyExists, outcomes["sku"]);
        Assert.Equal(IndexOutcome.Conflict, outcomes["store"]);
        Assert.Equal(IndexOutcome.Conflict, outcomes["at"]);
        Assert.Equal(IndexOutcome.WouldCreate, outcomes["code"]);
    }

    [Fact]
    public async Task The_same_keys_under_another_name_on_the_target_is_a_conflict()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku" });
        await CreateOnTargetAsync("{ sku: 1 }", new CreateIndexOptions { Name = "bySku" });

        var result = await CopyAsync();

        Assert.Equal(IndexOutcome.Conflict, Assert.Single(result.Indexes).Outcome);
    }

    [Fact]
    public async Task An_index_the_target_data_breaks_fails()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku", Unique = true });
        await Collection(_target).InsertManyAsync(
            [new BsonDocument("sku", "A1"), new BsonDocument("sku", "A1")],
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await CopyAsync();

        var index = Assert.Single(result.Indexes);
        Assert.Equal(IndexOutcome.Failed, index.Outcome);
        Assert.Contains("duplicate key", index.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_target_is_created_like_a_clustered_source()
    {
        await mongo.Client.GetDatabase(_source.Database).RunCommandAsync<BsonDocument>(
            BsonDocument.Parse($"{{ create: '{_source.Collection}', clusteredIndex: {{ key: {{ _id: 1 }}, unique: true, name: 'byId' }} }}"),
            cancellationToken: TestContext.Current.CancellationToken);
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku" });

        var result = await CopyAsync();

        Assert.True(result.CollectionCreated);
        Assert.Equal("sku", Assert.Single(result.Indexes).Name);
        Assert.Equal(IndexOutcome.Created, result.Indexes[0].Outcome);
        Assert.Equal(
            (await CollectionInfoAsync(_source))["options"]["clusteredIndex"],
            (await CollectionInfoAsync(_target))["options"]["clusteredIndex"]);
    }

    [Fact]
    public async Task A_missing_target_is_created_like_a_time_series_source()
    {
        await mongo.Client.GetDatabase(_source.Database).CreateCollectionAsync(
            _source.Collection,
            new CreateCollectionOptions { TimeSeriesOptions = new TimeSeriesOptions("at", "sensor", TimeSeriesGranularity.Minutes) },
            TestContext.Current.CancellationToken);
        await CreateAsync("{ 'sensor.site': 1, at: -1 }", new CreateIndexOptions { Name = "bySite" });

        var result = await CopyAsync();

        Assert.All(result.Indexes, i => Assert.NotEqual(IndexOutcome.Failed, i.Outcome));
        var target = await CollectionInfoAsync(_target);
        Assert.Equal("timeseries", target["type"].AsString);
        Assert.Equal((await CollectionInfoAsync(_source))["options"]["timeseries"], target["options"]["timeseries"]);
        Assert.Contains("bySite", (await IndexesAsync(_target)).Keys);
    }

    [Fact]
    public async Task A_target_that_is_a_view_fails_alone()
    {
        await CreateAsync("{ sku: 1 }", new CreateIndexOptions { Name = "sku" });
        var view = new MongoNamespace(MongoFixture.NewDatabase("view"), "Pump");
        await mongo.Client.GetDatabase(view.Database).CreateViewAsync<BsonDocument, BsonDocument>(
            view.Collection, "elsewhere", PipelineDefinition<BsonDocument, BsonDocument>.Create(Array.Empty<BsonDocument>()),
            cancellationToken: TestContext.Current.CancellationToken);

        var onView = await CopyAsync(target: view);
        var onCollection = await CopyAsync();

        Assert.NotNull(onView.Error);
        Assert.Empty(onView.Indexes);
        Assert.Null(onCollection.Error);
        Assert.Equal(IndexOutcome.Created, Assert.Single(onCollection.Indexes).Outcome);
    }

    [Fact]
    public async Task A_source_that_is_a_view_is_refused()
    {
        await mongo.Client.GetDatabase(_source.Database).CreateViewAsync<BsonDocument, BsonDocument>(
            _source.Collection, "elsewhere", PipelineDefinition<BsonDocument, BsonDocument>.Create(Array.Empty<BsonDocument>()),
            cancellationToken: TestContext.Current.CancellationToken);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IndexCopier(mongo.Client).ReadSourceAsync(_source, TestContext.Current.CancellationToken));

        Assert.Contains("is a view", e.Message, StringComparison.Ordinal);
    }

    private Task CreateOnTargetAsync(string keys, CreateIndexOptions options) =>
        Collection(_target).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(BsonDocument.Parse(keys), options),
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<BsonDocument> CollectionInfoAsync(MongoNamespace ns)
    {
        var cursor = await mongo.Client.GetDatabase(ns.Database).ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", ns.Collection) },
            TestContext.Current.CancellationToken);
        return await cursor.SingleAsync(TestContext.Current.CancellationToken);
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

namespace PipelineCopier.Tests;

public sealed class SavedPipelinesTests : IDisposable
{
    private static readonly MongoNamespace Source = new("ThirdApi", "Pump");
    private static readonly MongoNamespace PreProd = new("PreProd", "Pump");
    private static readonly MongoNamespace Staging = new("Staging", "Pump");
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly CompassFolderFixture _folder = new();

    private PipelineCopyReport Copy(bool mirror = false, bool dryRun = false, params MongoNamespace[] targets) =>
        new SavedPipelines(_folder.Path, new FixedTime(Now))
            .Copy(Source, targets.Length == 0 ? [PreProd, Staging] : targets, mirror, dryRun);

    [Fact]
    public void Each_target_gets_a_copy_of_every_source_pipeline()
    {
        _folder.Save("ThirdApi.Pump", "Bulk items");
        _folder.Save("ThirdApi.Pump", "Prices");

        var report = Copy();

        Assert.Equal(2, report.SourceCount);
        Assert.Equal(["Bulk items", "Prices"], _folder.Names("PreProd.Pump"));
        Assert.Equal(["Bulk items", "Prices"], _folder.Names("Staging.Pump"));
        Assert.Equal(["Bulk items", "Prices"], _folder.Names("ThirdApi.Pump"));
    }

    [Fact]
    public void A_copy_is_a_new_pipeline_with_everything_else_kept()
    {
        var sourceId = _folder.Save("ThirdApi.Pump", "Prices", pipelineText: "[{ $match: { price: { $gt: 0 } } }]", withMongoId: true);

        Copy(targets: PreProd);

        var copy = Assert.Single(_folder.Pipelines(), p => (string?)p.Json["namespace"] == "PreProd.Pump");
        var id = (string)copy.Json["id"]!;
        Assert.NotEqual(sourceId, id);
        Assert.Equal($"{id}.json", copy.FileName);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), (long)copy.Json["lastModified"]!);
        Assert.False(copy.Json.ContainsKey("_id"));
        Assert.Equal("[{ $match: { price: { $gt: 0 } } }]", (string)copy.Json["pipelineText"]!);
        Assert.Equal("localhost:27017", (string)copy.Json["host"]!);
        Assert.True((bool)copy.Json["autoPreview"]!);
    }

    [Fact]
    public void Running_again_replaces_the_copies_instead_of_adding_more()
    {
        _folder.Save("ThirdApi.Pump", "Prices");
        Copy();

        var report = Copy();

        Assert.Equal(["Prices"], _folder.Names("PreProd.Pump"));
        Assert.All(report.Targets, t => Assert.Equal(1, t.Replaced));
        Assert.Equal(3, _folder.Pipelines().Count);
    }

    [Fact]
    public void A_target_pipeline_with_a_source_pipeline_name_is_replaced()
    {
        _folder.Save("ThirdApi.Pump", "Prices", pipelineText: "[{ $match: { new: true } }]");
        _folder.Save("PreProd.Pump", "Prices", pipelineText: "[{ $match: { old: true } }]");

        Copy(targets: PreProd);

        var copy = Assert.Single(_folder.Pipelines(), p => (string?)p.Json["namespace"] == "PreProd.Pump");
        Assert.Equal("[{ $match: { new: true } }]", (string)copy.Json["pipelineText"]!);
    }

    [Fact]
    public void Pipelines_made_on_a_target_are_kept()
    {
        // The first version deleted every pipeline saved for a target before copying
        _folder.Save("ThirdApi.Pump", "Prices");
        _folder.Save("PreProd.Pump", "Made on PreProd");

        var report = Copy(targets: PreProd);

        Assert.Equal(["Made on PreProd", "Prices"], _folder.Names("PreProd.Pump"));
        Assert.Equal(["Made on PreProd"], Assert.Single(report.Targets).Kept);
    }

    [Fact]
    public void Mirror_deletes_the_pipelines_the_source_does_not_have()
    {
        _folder.Save("ThirdApi.Pump", "Prices");
        _folder.Save("PreProd.Pump", "Made on PreProd");
        _folder.Save("PreProd.Tos", "Another collection");

        var report = Copy(mirror: true, targets: PreProd);

        Assert.Equal(["Prices"], _folder.Names("PreProd.Pump"));
        Assert.Equal(["Another collection"], _folder.Names("PreProd.Tos"));
        Assert.Equal(["Made on PreProd"], Assert.Single(report.Targets).Removed);
    }

    [Fact]
    public void A_source_with_nothing_saved_changes_no_target()
    {
        // The first version deleted the targets' pipelines and then found nothing to copy
        _folder.Save("PreProd.Pump", "Made on PreProd");

        var report = Copy(mirror: true);

        Assert.Equal(0, report.SourceCount);
        Assert.Empty(report.Targets);
        Assert.Equal(["Made on PreProd"], _folder.Names("PreProd.Pump"));
    }

    [Fact]
    public void A_dry_run_changes_nothing_and_says_what_it_would_do()
    {
        _folder.Save("ThirdApi.Pump", "Prices");
        _folder.Save("PreProd.Pump", "Prices");
        _folder.Save("PreProd.Pump", "Made on PreProd");
        var before = Directory.GetFiles(_folder.Path).Order().ToList();

        var report = Copy(mirror: true, dryRun: true, targets: PreProd);

        Assert.Equal(before, Directory.GetFiles(_folder.Path).Order().ToList());
        var target = Assert.Single(report.Targets);
        Assert.Equal(["Prices"], target.Copied);
        Assert.Equal(1, target.Replaced);
        Assert.Equal(["Made on PreProd"], target.Removed);
    }

    [Fact]
    public void Files_that_are_not_saved_pipelines_are_left_alone()
    {
        _folder.Save("ThirdApi.Pump", "Prices");
        _folder.SaveFile("broken.json", "{ not json");
        _folder.SaveFile("array.json", "[]");
        _folder.SaveFile("settings.json", """{ "theme": "dark" }""");

        var report = Copy(mirror: true);

        Assert.Equal(3, report.Unreadable.Count);
        Assert.Contains(report.Unreadable, u => u.StartsWith("broken.json:", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "broken.json")));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "array.json")));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "settings.json")));
    }

    [Fact]
    public void A_collection_name_with_dots_is_matched_whole()
    {
        _folder.Save("Shop.orders.2024", "Totals");
        _folder.Save("Shop.orders", "Not this one");

        new SavedPipelines(_folder.Path, new FixedTime(Now))
            .Copy(MongoNamespace.Parse("Shop.orders.2024"), [MongoNamespace.Parse("Archive.orders.2024")], mirror: false, dryRun: false);

        Assert.Equal(["Totals"], _folder.Names("Archive.orders.2024"));
    }

    public void Dispose() => _folder.Dispose();
}

using MongoDB.Bson;
using MongoDB.Driver;

namespace PipelineCopier.Tests;

/// <summary>The command line from end to end, with a folder of its own and the MongoDB in a container.</summary>
public sealed class AppTests(MongoFixture mongo) : IDisposable
{
    private readonly CompassFolderFixture _folder = new();

    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = await App.RunAsync(
            [.. args, "--connection", mongo.ConnectionString, "--compass-folder", _folder.Path],
            output,
            TestContext.Current.CancellationToken);
        return (exitCode, output.ToString());
    }

    [Fact]
    public async Task Copies_the_pipelines_and_the_indexes_and_reports_usage()
    {
        var source = MongoFixture.NewDatabase("source");
        var target = MongoFixture.NewDatabase("target");
        _folder.Save($"{source}.Pump", "Prices");
        await mongo.Client.GetDatabase(source).GetCollection<BsonDocument>("Pump").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(BsonDocument.Parse("{ sku: 1 }"), new CreateIndexOptions { Name = "sku" }),
            cancellationToken: TestContext.Current.CancellationToken);

        var (exitCode, output) = await RunAsync(source, target);

        Assert.True(exitCode == App.Succeeded, output);
        Assert.Equal(["Prices"], _folder.Names($"{target}.Pump"));
        Assert.Contains("✓ sku", output, StringComparison.Ordinal);
        Assert.Contains("- sku: 0 ops", output, StringComparison.Ordinal);
        Assert.Contains("Restart MongoDB Compass", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Full_namespaces_name_other_collections()
    {
        var source = MongoFixture.NewDatabase("source");
        _folder.Save($"{source}.orders.2024", "Totals");

        var (exitCode, output) = await RunAsync($"{source}.orders.2024", "Archive.orders.2024", "--skip", "indexes", "report");

        Assert.True(exitCode == App.Succeeded, output);
        Assert.Equal(["Totals"], _folder.Names("Archive.orders.2024"));
        Assert.DoesNotContain("Indexes (MongoDB)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_source_cannot_also_be_a_target()
    {
        _folder.Save("ThirdApi.Pump", "Prices");

        var (exitCode, output) = await RunAsync("ThirdApi", "PreProd", "ThirdApi");

        Assert.Equal(App.InvalidArguments, exitCode);
        Assert.Contains("ThirdApi.Pump is both the source and a target", output, StringComparison.Ordinal);
        Assert.Equal(["Prices"], _folder.Names("ThirdApi.Pump"));
    }

    [Fact]
    public async Task A_server_that_is_not_there_fails_the_run_without_showing_the_password()
    {
        var output = new StringWriter();

        var exitCode = await App.RunAsync(
            ["--connection", "mongodb://reader:secret@127.0.0.1:1/?serverSelectionTimeoutMS=500", "--compass-folder", _folder.Path],
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(App.Failed, exitCode);
        Assert.Contains("Could not reach MongoDB at 127.0.0.1:1", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_string_that_is_not_valid_fails_the_run_without_repeating_it()
    {
        var output = new StringWriter();

        var exitCode = await App.RunAsync(
            ["--connection", "mongodb://reader:secret@", "--compass-folder", _folder.Path, "--skip", "pipelines"],
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(App.Failed, exitCode);
        Assert.Contains("connection string is not valid", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _folder.Dispose();
}

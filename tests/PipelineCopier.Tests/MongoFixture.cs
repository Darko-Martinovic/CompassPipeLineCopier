using MongoDB.Driver;
using PipelineCopier.Tests;
using Testcontainers.MongoDb;

[assembly: AssemblyFixture(typeof(MongoFixture))]

namespace PipelineCopier.Tests;

/// <summary>One MongoDB in a container for every test; each test works in databases of its own.</summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:8.0").Build();

    public string ConnectionString => _mongo.GetConnectionString();

    public IMongoClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _mongo.StartAsync();
        }
        catch (Exception e)
        {
            // Show why MongoDB didn't start, not only that it didn't
            var (stdout, stderr) = await _mongo.GetLogsAsync();
            throw new InvalidOperationException($"MongoDB didn't start:\n{stdout}\n{stderr}", e);
        }

        Client = new MongoClient(ConnectionString);
    }

    /// <summary>A database name no other test uses.</summary>
    public static string NewDatabase(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..30];

    public async ValueTask DisposeAsync()
    {
        (Client as IDisposable)?.Dispose();
        await _mongo.DisposeAsync();
    }
}

using System.Text.Json.Nodes;

namespace PipelineCopier.Tests;

/// <summary>A SavedPipelines folder of its own for one test, deleted afterwards.</summary>
public sealed class CompassFolderFixture : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("SavedPipelines").FullName;

    /// <summary>Saves a pipeline the way Compass does: a file named after its id.</summary>
    public string Save(string ns, string name, string pipelineText = "[{ $match: {} }]", bool withMongoId = false)
    {
        var id = Guid.NewGuid().ToString();
        var json = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["namespace"] = ns,
            ["comments"] = true,
            ["autoPreview"] = true,
            ["collationString"] = "",
            ["pipelineText"] = pipelineText,
            ["host"] = "localhost:27017",
            ["lastModified"] = 1,
        };
        if (withMongoId)
        {
            json["_id"] = id;
        }

        File.WriteAllText(System.IO.Path.Combine(Path, $"{id}.json"), json.ToJsonString());
        return id;
    }

    public void SaveFile(string fileName, string content) =>
        File.WriteAllText(System.IO.Path.Combine(Path, fileName), content);

    /// <summary>Every saved pipeline, by file name.</summary>
    public IReadOnlyList<(string FileName, JsonObject Json)> Pipelines() =>
        Directory.EnumerateFiles(Path)
            .Select(f => (System.IO.Path.GetFileName(f), JsonNode.Parse(File.ReadAllText(f))))
            .Where(p => p.Item2 is JsonObject json && json.ContainsKey("namespace"))
            .Select(p => (p.Item1, p.Item2!.AsObject()))
            .ToList();

    public IReadOnlyList<string> Names(string ns) =>
        Pipelines()
            .Where(p => (string?)p.Json["namespace"] == ns)
            .Select(p => (string)p.Json["name"]!)
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>A clock that stays where it is put.</summary>
public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

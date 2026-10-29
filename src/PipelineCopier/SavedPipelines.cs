using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PipelineCopier;

/// <summary>What happened to one target's saved pipelines.</summary>
/// <param name="Copied">The names of the source pipelines written for the target.</param>
/// <param name="Replaced">How many of the target's pipelines had a source pipeline's name and were replaced.</param>
/// <param name="Removed">The target's pipelines that --mirror deleted because the source has none of that name.</param>
/// <param name="Kept">The target's pipelines left as they were because the source has none of that name.</param>
/// <param name="Error">Why writing or deleting stopped part way, if it did.</param>
public sealed record PipelineCopyResult(
    MongoNamespace Target,
    IReadOnlyList<string> Copied,
    int Replaced,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    string? Error = null);

/// <param name="Unreadable">Files in the folder that aren't saved pipelines, with the reason; they are never changed.</param>
public sealed record PipelineCopyReport(
    int SourceCount,
    IReadOnlyList<PipelineCopyResult> Targets,
    IReadOnlyList<string> Unreadable);

/// <summary>
/// Copies the pipelines Compass saved for one namespace to other namespaces, in Compass's
/// SavedPipelines folder.
/// </summary>
/// <remarks>
/// A copy replaces the target's pipelines of the same name, so running the copy again gives the
/// same result. The target's other pipelines are kept, unless mirror is asked for.
/// </remarks>
public sealed class SavedPipelines(string folder, TimeProvider time)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Compass writes the pipeline text with plain quotes and angle brackets, and so do we
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Saved(string FilePath, string Name, string Namespace, JsonObject Json);

    public string Folder => folder;

    public bool Exists => Directory.Exists(folder);

    public PipelineCopyReport Copy(MongoNamespace source, IReadOnlyList<MongoNamespace> targets, bool mirror, bool dryRun)
    {
        var (saved, unreadable) = Load();
        var fromSource = saved.Where(p => p.Namespace == source.ToString()).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

        // A source with nothing saved (a typo, or the wrong folder) changes nothing anywhere
        if (fromSource.Count == 0)
        {
            return new PipelineCopyReport(0, [], unreadable);
        }

        var sourceNames = fromSource.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var lastModified = time.GetUtcNow().ToUnixTimeMilliseconds();
        var results = new List<PipelineCopyResult>();

        foreach (var target in targets)
        {
            var inTarget = saved.Where(p => p.Namespace == target.ToString()).ToList();
            var replaced = inTarget.Where(p => sourceNames.Contains(p.Name)).ToList();
            var others = inTarget.Where(p => !sourceNames.Contains(p.Name)).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
            string? error = null;

            if (!dryRun)
            {
                try
                {
                    // The copies are written before anything is deleted, so a failure part way loses nothing
                    foreach (var pipeline in fromSource)
                    {
                        Write(CopyOf(pipeline.Json, target, lastModified));
                    }

                    foreach (var pipeline in replaced)
                    {
                        File.Delete(pipeline.FilePath);
                    }

                    if (mirror)
                    {
                        foreach (var pipeline in inTarget.Except(replaced))
                        {
                            File.Delete(pipeline.FilePath);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error = e.Message;
                }
            }

            results.Add(new PipelineCopyResult(
                target,
                fromSource.Select(p => p.Name).ToList(),
                replaced.Count,
                mirror ? others : [],
                mirror ? [] : others,
                error));
        }

        return new PipelineCopyReport(fromSource.Count, results, unreadable);
    }

    private (List<Saved> Saved, List<string> Unreadable) Load()
    {
        var saved = new List<Saved>();
        var unreadable = new List<string>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json)
                {
                    unreadable.Add($"{fileName}: not a JSON object");
                }
                else if (Text(json, "namespace") is not { } ns)
                {
                    unreadable.Add($"{fileName}: no namespace");
                }
                else
                {
                    saved.Add(new Saved(file, Text(json, "name") ?? "(unnamed)", ns, json));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{fileName}: {e.Message}");
            }
        }

        return (saved, unreadable);
    }

    private static string? Text(JsonObject json, string property) =>
        json[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static JsonObject CopyOf(JsonObject pipeline, MongoNamespace target, long lastModified)
    {
        var copy = pipeline.DeepClone().AsObject();
        copy["id"] = Guid.NewGuid().ToString();
        copy["namespace"] = target.ToString();
        copy["lastModified"] = lastModified;
        // Compass won't load a saved pipeline that has an _id
        copy.Remove("_id");
        return copy;
    }

    private void Write(JsonObject pipeline)
    {
        // Compass names each file after the pipeline's id. The file is written under another name
        // first, which Compass doesn't read, so it never sees half a file
        var path = Path.Combine(folder, $"{Text(pipeline, "id")}.json");
        var partial = path + ".partial";
        File.WriteAllText(partial, pipeline.ToJsonString(WriteOptions));
        File.Move(partial, path);
    }
}

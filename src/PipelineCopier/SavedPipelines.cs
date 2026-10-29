using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PipelineCopier;

/// <summary>What happened to one target's saved pipelines.</summary>
/// <param name="Copied">The names of the source pipelines written for the target.</param>
/// <param name="Replaced">How many of the target's pipelines had a source pipeline's name and were replaced.</param>
/// <param name="Removed">The target's pipelines that --mirror deleted because the source has none of that name.</param>
/// <param name="Kept">The target's pipelines left as they were because the source has none of that name.</param>
/// <param name="Error">Why the copy stopped part way, if it did; the counts are then what was done before it stopped.</param>
public sealed record PipelineCopyResult(
    MongoNamespace Target,
    IReadOnlyList<string> Copied,
    int Replaced,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    string? Error = null);

/// <param name="Unreadable">Files in the folder that aren't saved pipelines, with the reason; they are never changed.</param>
/// <param name="Unnamed">How many source pipelines have no name, and so were not copied.</param>
/// <param name="DuplicateNames">Names more than one source pipeline has; each target gets all of them.</param>
public sealed record PipelineCopyReport(
    int SourceCount,
    IReadOnlyList<PipelineCopyResult> Targets,
    IReadOnlyList<string> Unreadable,
    int Unnamed = 0,
    IReadOnlyList<string>? DuplicateNames = null);

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
    private const string PartialSuffix = ".partial";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Compass writes the pipeline text with plain quotes and angle brackets, and so do we
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // A file with the same property twice is not one Compass wrote, and reading it would be a guess
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowDuplicateProperties = false };

    private sealed record Saved(string FilePath, string? Name, string Namespace, JsonObject Json);

    public string Folder => folder;

    public bool Exists => Directory.Exists(folder);

    public PipelineCopyReport Copy(
        MongoNamespace source, IReadOnlyList<MongoNamespace> targets, bool mirror, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (!dryRun)
        {
            RemovePartialFiles();
        }

        var (saved, unreadable) = Load();
        var inSource = saved.Where(p => p.Namespace == source.ToString()).ToList();

        // Compass always names a saved pipeline; one without a name can't be matched on a target
        var fromSource = inSource.Where(p => p.Name is not null).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var unnamed = inSource.Count - fromSource.Count;
        var duplicates = fromSource.GroupBy(p => p.Name!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        // A source with nothing saved (a typo, or the wrong folder) changes nothing anywhere
        if (fromSource.Count == 0)
        {
            return new PipelineCopyReport(0, [], unreadable, unnamed, duplicates);
        }

        var sourceNames = fromSource.Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
        var lastModified = time.GetUtcNow().ToUnixTimeMilliseconds();
        var results = new List<PipelineCopyResult>();

        foreach (var target in targets)
        {
            // Stopping between targets leaves each one either copied or untouched
            cancellationToken.ThrowIfCancellationRequested();

            var inTarget = saved.Where(p => p.Namespace == target.ToString()).ToList();
            var replaced = inTarget.Where(p => p.Name is not null && sourceNames.Contains(p.Name)).ToList();
            var others = inTarget.Except(replaced).ToList();

            results.Add(dryRun
                ? new PipelineCopyResult(target, Names(fromSource), replaced.Count, mirror ? Names(others) : [], mirror ? [] : Names(others))
                : Apply(target, fromSource, replaced, others, mirror, lastModified));
        }

        return new PipelineCopyReport(fromSource.Count, results, unreadable, unnamed, duplicates);
    }

    private PipelineCopyResult Apply(
        MongoNamespace target, List<Saved> fromSource, List<Saved> replaced, List<Saved> others, bool mirror, long lastModified)
    {
        var kept = mirror ? [] : Names(others);
        var written = new List<string>();
        try
        {
            // The copies are written before anything is deleted, so a failure part way loses nothing
            foreach (var pipeline in fromSource)
            {
                written.Add(Write(CopyOf(pipeline.Json, target, lastModified)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Take back the copies that were written, so the target is as it was
            foreach (var path in written)
            {
                TryDelete(path);
            }

            return new PipelineCopyResult(target, [], 0, [], Names(others), e.Message);
        }

        var replacedCount = 0;
        var removed = new List<string>();
        try
        {
            foreach (var pipeline in replaced)
            {
                File.Delete(pipeline.FilePath);
                replacedCount++;
            }

            if (mirror)
            {
                foreach (var pipeline in others)
                {
                    File.Delete(pipeline.FilePath);
                    removed.Add(pipeline.Name ?? "(unnamed)");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Every copy is there; some of what they replace is too, until the next run
            return new PipelineCopyResult(target, Names(fromSource), replacedCount, removed, kept, e.Message);
        }

        return new PipelineCopyResult(target, Names(fromSource), replacedCount, removed, kept);
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
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: ReadOptions) is not JsonObject json)
                {
                    unreadable.Add($"{fileName}: not a JSON object");
                }
                else if (Text(json, "namespace") is not { } ns)
                {
                    unreadable.Add($"{fileName}: no namespace");
                }
                else
                {
                    saved.Add(new Saved(file, Text(json, "name"), ns, json));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{fileName}: {e.Message}");
            }
        }

        return (saved, unreadable);
    }

    // A run that was stopped while writing a copy leaves it under its temporary name
    private void RemovePartialFiles()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.json" + PartialSuffix))
        {
            TryDelete(file);
        }
    }

    private static List<string> Names(IEnumerable<Saved> pipelines) =>
        pipelines.Select(p => p.Name ?? "(unnamed)").Order(StringComparer.Ordinal).ToList();

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

    /// <summary>Writes a pipeline and returns the file's path.</summary>
    private string Write(JsonObject pipeline)
    {
        // Compass names each file after the pipeline's id. The file is written under another name
        // first, which Compass doesn't read, so it never sees half a file
        var path = Path.Combine(folder, $"{Text(pipeline, "id")}.json");
        var partial = path + PartialSuffix;
        File.WriteAllText(partial, pipeline.ToJsonString(WriteOptions));
        File.Move(partial, path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next run
        }
    }
}

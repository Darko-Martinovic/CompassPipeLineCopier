using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;

// Configuration
var sourceNamespace = "ThirdApi.Pump";
var targetNamespaces = new[] { "PreProd.Pump", "Staging.Pump" };
var mongoConnectionString = "mongodb://localhost:27017";

Console.WriteLine("MongoDB Compass Pipeline & Index Copier");
Console.WriteLine("=======================================");
Console.WriteLine($"Source: {sourceNamespace}");
Console.WriteLine($"Targets: {string.Join(", ", targetNamespaces)}");
Console.WriteLine();

// ============================================
// PART 1: Copy Saved Pipelines (Compass)
// ============================================
Console.WriteLine("PART 1: Copying Saved Pipelines");
Console.WriteLine("-------------------------------");

var compassPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MongoDB Compass");

var savedPipelinesPath = Path.Combine(compassPath, "SavedPipelines");

if (!Directory.Exists(savedPipelinesPath))
{
    Console.WriteLine($"SavedPipelines folder not found at: {savedPipelinesPath}");
}
else
{
    Console.WriteLine($"Folder: {savedPipelinesPath}\n");

    // Clean up previously created files for all targets
    var existingFiles = Directory.GetFiles(savedPipelinesPath, "*.json");
    var deleted = 0;
    foreach (var file in existingFiles)
    {
        try
        {
            var content = File.ReadAllText(file);
            var json = JsonNode.Parse(content);
            var ns = json?["namespace"]?.GetValue<string>();
            if (ns != null && targetNamespaces.Contains(ns))
            {
                File.Delete(file);
                deleted++;
            }
        }
        catch { }
    }
    if (deleted > 0)
    {
        Console.WriteLine($"Cleaned up {deleted} previous target files\n");
    }

    // Process and duplicate to each target
    var files = Directory.GetFiles(savedPipelinesPath, "*.json");
    var unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    foreach (var targetNamespace in targetNamespaces)
    {
        Console.WriteLine($"  → {targetNamespace}:");
        var duplicated = 0;

        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var json = JsonNode.Parse(content);

                if (json == null) continue;

                var ns = json["namespace"]?.GetValue<string>();

                if (ns == sourceNamespace)
                {
                    var name = json["name"]?.GetValue<string>() ?? "unnamed";

                    var newId = Guid.NewGuid().ToString();
                    var newFileName = $"{newId}.json";
                    var newFilePath = Path.Combine(savedPipelinesPath, newFileName);

                    json["id"] = newId;
                    json["namespace"] = targetNamespace;
                    json["lastModified"] = unixTimestamp;

                    if (json.AsObject().ContainsKey("_id"))
                    {
                        json.AsObject().Remove("_id");
                    }

                    File.WriteAllText(newFilePath, json.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }));

                    Console.WriteLine($"      ✓ {name}");
                    duplicated++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ✗ Error: {ex.Message}");
            }
        }
        Console.WriteLine($"    Duplicated: {duplicated} pipelines\n");
    }
}

// ============================================
// PART 2: Copy Indexes (MongoDB)
// ============================================
Console.WriteLine("PART 2: Copying Indexes");
Console.WriteLine("-----------------------");

try
{
    var client = new MongoClient(mongoConnectionString);

    var sourceParts = sourceNamespace.Split('.');
    var sourceDb = client.GetDatabase(sourceParts[0]);
    var sourceCollection = sourceDb.GetCollection<BsonDocument>(sourceParts[1]);

    // Get source indexes
    var sourceIndexes = sourceCollection.Indexes.List().ToList();
    Console.WriteLine($"Source indexes in {sourceNamespace}: {sourceIndexes.Count}");

    foreach (var idx in sourceIndexes)
    {
        var name = idx.GetValue("name", "unknown").AsString;
        var key = idx.GetValue("key", new BsonDocument()).ToBsonDocument();
        Console.WriteLine($"  - {name}: {key}");
    }
    Console.WriteLine();

    foreach (var targetNamespace in targetNamespaces)
    {
        Console.WriteLine($"  → {targetNamespace}:");

        var targetParts = targetNamespace.Split('.');
        var targetDb = client.GetDatabase(targetParts[0]);
        var targetCollection = targetDb.GetCollection<BsonDocument>(targetParts[1]);

        var created = 0;
        var skipped = 0;

        foreach (var idx in sourceIndexes)
        {
            var name = idx.GetValue("name", "unknown").AsString;

            // Skip the default _id index
            if (name == "_id_")
            {
                skipped++;
                continue;
            }

            try
            {
                var key = idx.GetValue("key").ToBsonDocument();
                var options = new CreateIndexOptions<BsonDocument> { Name = name };

                // Copy common index options if they exist
                if (idx.Contains("unique") && idx["unique"].AsBoolean)
                    options.Unique = true;
                if (idx.Contains("sparse") && idx["sparse"].AsBoolean)
                    options.Sparse = true;
                if (idx.Contains("expireAfterSeconds"))
                    options.ExpireAfter = TimeSpan.FromSeconds(idx["expireAfterSeconds"].AsInt32);
                if (idx.Contains("partialFilterExpression"))
                    options.PartialFilterExpression = new BsonDocumentFilterDefinition<BsonDocument>(idx["partialFilterExpression"].ToBsonDocument());

                var indexModel = new CreateIndexModel<BsonDocument>(key, options);
                targetCollection.Indexes.CreateOne(indexModel);

                Console.WriteLine($"      ✓ {name}");
                created++;
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
            {
                Console.WriteLine($"      ○ {name} (already exists)");
                skipped++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ✗ {name}: {ex.Message}");
            }
        }
        Console.WriteLine($"    Created: {created}, Skipped: {skipped}\n");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"MongoDB connection error: {ex.Message}");
    Console.WriteLine("Make sure MongoDB is running on localhost:27017");
}

// ============================================
// PART 3: Check for Missing Indexes
// ============================================
Console.WriteLine("PART 3: Index Usage Analysis (Source)");
Console.WriteLine("-------------------------------------");

try
{
    var client = new MongoClient(mongoConnectionString);
    var sourceParts = sourceNamespace.Split('.');
    var sourceDb = client.GetDatabase(sourceParts[0]);
    var sourceCollection = sourceDb.GetCollection<BsonDocument>(sourceParts[1]);

    // Get index stats
    var pipeline = new[] { new BsonDocument("$indexStats", new BsonDocument()) };
    var indexStats = sourceCollection.Aggregate<BsonDocument>(pipeline).ToList();

    Console.WriteLine("Index usage statistics:");
    foreach (var stat in indexStats)
    {
        var name = stat.GetValue("name", "unknown").AsString;
        var accesses = stat.GetValue("accesses", new BsonDocument()).ToBsonDocument();
        var ops = accesses.GetValue("ops", 0).ToInt64();
        var since = accesses.GetValue("since", BsonDateTime.Create(DateTime.MinValue)).ToUniversalTime();

        Console.WriteLine($"  - {name}: {ops} ops since {since:yyyy-MM-dd HH:mm}");
    }

    // Check profiling status
    Console.WriteLine("\nTo find missing indexes, enable profiling:");
    Console.WriteLine("  db.setProfilingLevel(1, { slowms: 100 })");
    Console.WriteLine("\nThen check slow queries:");
    Console.WriteLine("  db.system.profile.find({ ns: '" + sourceNamespace + "' }).sort({ ts: -1 }).limit(10)");
    Console.WriteLine("\nLook for 'planSummary: COLLSCAN' which indicates missing indexes.");
}
catch (Exception ex)
{
    Console.WriteLine($"Could not get index stats: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=======================================");
Console.WriteLine("Done! Restart MongoDB Compass to see pipelines.");

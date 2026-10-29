namespace PipelineCopier;

/// <summary>
/// A collection in a database, written the way Compass and MongoDB write it: database.collection.
/// </summary>
public readonly record struct MongoNamespace(string Database, string Collection)
{
    /// <summary>
    /// Reads database.collection. A database name can't contain a dot and a collection name can,
    /// so the first dot is the one that splits them: "Shop.orders.2024" is the collection
    /// "orders.2024" in "Shop".
    /// </summary>
    public static MongoNamespace Parse(string value)
    {
        var dot = value.IndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            throw new FormatException($"'{value}' is not a namespace of the form database.collection.");
        }

        return new MongoNamespace(value[..dot], value[(dot + 1)..]);
    }

    /// <summary>
    /// Reads a command-line argument: a full namespace when it has a dot, otherwise a database
    /// name for the collection given with --collection.
    /// </summary>
    public static MongoNamespace FromArgument(string value, string collection) =>
        value.Contains('.') ? Parse(value) : new MongoNamespace(value, collection);

    public override string ToString() => $"{Database}.{Collection}";
}

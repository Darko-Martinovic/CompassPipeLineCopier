using System.Text;

namespace PipelineCopier;

/// <summary>
/// A collection in a database, written the way Compass and MongoDB write it: database.collection.
/// </summary>
public readonly record struct MongoNamespace(string Database, string Collection)
{
    // MongoDB's rules for names, with the characters Windows servers also refuse
    private static readonly char[] NotInDatabaseNames = ['/', '\\', '.', ' ', '"', '$', '*', '<', '>', ':', '|', '?', '\0'];
    private const int MaxDatabaseBytes = 63;

    /// <summary>
    /// Reads database.collection. A database name can't contain a dot and a collection name can,
    /// so the first dot is the one that splits them: "Shop.orders.2024" is the collection
    /// "orders.2024" in "Shop".
    /// </summary>
    public static MongoNamespace Parse(string value)
    {
        Refuse(value);
        var dot = value.IndexOf('.');
        if (dot <= 0 || dot == value.Length - 1)
        {
            throw new FormatException($"'{value}' is not a namespace of the form database.collection.");
        }

        return Checked(value[..dot], value[(dot + 1)..]);
    }

    /// <summary>
    /// Reads a command-line argument: a full namespace when it has a dot, otherwise a database
    /// name for the collection given with --collection.
    /// </summary>
    public static MongoNamespace FromArgument(string value, string collection)
    {
        Refuse(value);
        return value.Contains('.') ? Parse(value) : Checked(value, collection);
    }

    public override string ToString() => $"{Database}.{Collection}";

    // A connection string given where a namespace belongs would otherwise be printed and saved
    // in a pipeline file, password and all; the message doesn't repeat it
    private static void Refuse(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal))
        {
            throw new FormatException("A connection string was given as a database or namespace. Pass it with --connection.");
        }
    }

    private static MongoNamespace Checked(string database, string collection)
    {
        if (database.Length == 0 || database.IndexOfAny(NotInDatabaseNames) >= 0 || Encoding.UTF8.GetByteCount(database) > MaxDatabaseBytes)
        {
            throw new FormatException(
                $"'{database}' is not a database name: it needs 1 to {MaxDatabaseBytes} bytes and none of / \\ . space \" $ * < > : | ?");
        }

        if (collection.Length == 0 || collection.Contains('$') || collection.Contains('\0'))
        {
            throw new FormatException($"'{collection}' is not a collection name: it can't be empty or contain $.");
        }

        if (collection.StartsWith("system.", StringComparison.Ordinal))
        {
            throw new FormatException($"'{collection}' is one of MongoDB's own collections, which this tool doesn't copy.");
        }

        return new MongoNamespace(database, collection);
    }
}

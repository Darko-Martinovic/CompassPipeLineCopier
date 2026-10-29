namespace PipelineCopier.Tests;

public sealed class MongoNamespaceTests
{
    [Fact]
    public void The_first_dot_splits_the_database_from_the_collection()
    {
        Assert.Equal(new MongoNamespace("Shop", "orders.2024"), MongoNamespace.Parse("Shop.orders.2024"));
    }

    [Theory]
    [InlineData("Shop")]
    [InlineData(".orders")]
    [InlineData("Shop.")]
    public void Anything_else_is_not_a_namespace(string value)
    {
        Assert.Throws<FormatException>(() => MongoNamespace.Parse(value));
    }

    [Fact]
    public void An_argument_without_a_dot_is_a_database_in_the_default_collection()
    {
        Assert.Equal(new MongoNamespace("PreProd", "Pump"), MongoNamespace.FromArgument("PreProd", "Pump"));
        Assert.Equal(new MongoNamespace("PreProd", "Tos"), MongoNamespace.FromArgument("PreProd.Tos", "Pump"));
    }

    [Theory]
    [InlineData("Pre Prod")]
    [InlineData("Pre/Prod")]
    [InlineData("Pre$Prod")]
    [InlineData("Pre:Prod")]
    [InlineData("")]
    [InlineData("a_database_name_that_is_far_longer_than_the_sixty_three_bytes_mongo_takes")]
    public void A_database_name_MongoDB_refuses_is_refused(string database)
    {
        Assert.Throws<FormatException>(() => MongoNamespace.FromArgument(database, "Pump"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("pu$mp")]
    [InlineData("system.profile")]
    public void A_collection_name_MongoDB_refuses_or_keeps_for_itself_is_refused(string collection)
    {
        Assert.Throws<FormatException>(() => MongoNamespace.FromArgument("PreProd", collection));
    }

    [Fact]
    public void A_connection_string_is_refused_without_being_repeated()
    {
        var e = Assert.Throws<FormatException>(() => MongoNamespace.FromArgument("mongodb://admin:secret@localhost/x", "Pump"));

        Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void It_is_written_as_database_dot_collection()
    {
        Assert.Equal("Shop.orders.2024", new MongoNamespace("Shop", "orders.2024").ToString());
    }
}

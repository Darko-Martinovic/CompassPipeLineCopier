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

    [Fact]
    public void It_is_written_as_database_dot_collection()
    {
        Assert.Equal("Shop.orders.2024", new MongoNamespace("Shop", "orders.2024").ToString());
    }
}

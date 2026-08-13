using MessageService.Services;

namespace MessageService.Tests.Services;

public class DatabaseProviderResolverTests
{
    [Theory]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public void ExplicitProvider_AlwaysWins_RegardlessOfConnectionString(string configuredProvider)
    {
        var (provider, wasInferred) = DatabaseProviderResolver.Resolve(configuredProvider, hasSqlServerConnectionString: true);

        Assert.Equal(configuredProvider, provider);
        Assert.False(wasInferred);
    }

    [Fact]
    public void Unconfigured_WithSqlServerConnectionString_InfersSqlServer()
    {
        var (provider, wasInferred) = DatabaseProviderResolver.Resolve(null, hasSqlServerConnectionString: true);

        Assert.Equal("SqlServer", provider);
        Assert.True(wasInferred);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unconfigured_WithoutSqlServerConnectionString_InfersSqlite(string? configuredProvider)
    {
        var (provider, wasInferred) = DatabaseProviderResolver.Resolve(configuredProvider, hasSqlServerConnectionString: false);

        Assert.Equal("Sqlite", provider);
        Assert.True(wasInferred);
    }
}

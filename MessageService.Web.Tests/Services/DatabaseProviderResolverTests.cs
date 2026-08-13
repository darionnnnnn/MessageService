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

    [Theory]
    [InlineData("sqlserver", "SqlServer")]
    [InlineData("SQLSERVER", "SqlServer")]
    [InlineData("sqlite", "Sqlite")]
    [InlineData("SQLITE", "Sqlite")]
    public void ExplicitProvider_CaseInsensitive_NormalizedToCanonicalForm(string configuredProvider, string expected)
    {
        // 下游（DbContext 註冊、migration、validator）全部用 == "SqlServer" 精確比對——
        // 大小寫必須在推導點收斂，不然 "sqlserver" 會靜默落入 Sqlite 分支且沒有任何警告
        var (provider, wasInferred) = DatabaseProviderResolver.Resolve(configuredProvider, hasSqlServerConnectionString: false);

        Assert.Equal(expected, provider);
        Assert.False(wasInferred);
    }

    [Fact]
    public void ExplicitProvider_UnknownValue_PassesThroughUnchanged()
    {
        // 無法辨認的值維持原樣——下游行為跟既往一致（非 SqlServer 一律走 Sqlite 分支）
        var (provider, wasInferred) = DatabaseProviderResolver.Resolve("Postgres", hasSqlServerConnectionString: true);

        Assert.Equal("Postgres", provider);
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

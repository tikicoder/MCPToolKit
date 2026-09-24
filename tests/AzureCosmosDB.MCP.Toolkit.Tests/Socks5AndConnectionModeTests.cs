using AzureCosmosDB.MCP.Toolkit.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AzureCosmosDB.MCP.Toolkit.Tests;

public class Socks5AndConnectionModeTests
{
    [Theory]
    [InlineData("socks5://127.0.0.1:51080", "127.0.0.1", 51080)]
    [InlineData("socks5h://localhost:1080", "localhost", 1080)]
    [InlineData("127.0.0.1:51080", "127.0.0.1", 51080)]
    public void TryParseProxyUrl_AcceptsSocks5Forms(string raw, string host, int port)
    {
        Assert.True(Socks5Connect.TryParseProxyUrl(raw, out var h, out var p, out var err), err);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("socks5://host")]
    [InlineData("")]
    public void TryParseProxyUrl_RejectsBadForms(string raw)
    {
        Assert.False(Socks5Connect.TryParseProxyUrl(raw, out _, out _, out _));
    }

    [Fact]
    public void ResolveSocks5Proxy_PrefersExplicitEnv()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["COSMOS_SOCKS5_PROXY"] = "socks5://127.0.0.1:51080",
                ["ALL_PROXY"] = "socks5://127.0.0.1:9999"
            })
            .Build();
        Assert.Equal("socks5://127.0.0.1:51080", CosmosClientFactory.ResolveSocks5Proxy(config));
    }

    [Fact]
    public void ResolveConnectionMode_SocksForcesGateway()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.Equal(
            ConnectionMode.Gateway,
            CosmosClientFactory.ResolveConnectionMode(config, "socks5://127.0.0.1:51080"));
    }

    [Fact]
    public void ResolveConnectionMode_DirectWithSocksThrows()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["COSMOS_CONNECTION_MODE"] = "Direct"
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() =>
            CosmosClientFactory.ResolveConnectionMode(config, "socks5://127.0.0.1:51080"));
    }
}

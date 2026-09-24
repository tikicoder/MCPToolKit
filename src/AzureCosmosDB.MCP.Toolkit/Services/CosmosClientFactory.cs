using Azure.Identity;
using Microsoft.Azure.Cosmos;
using System.Net.Http;

namespace AzureCosmosDB.MCP.Toolkit.Services;

/// <summary>
/// Factory for creating CosmosClient instances with support for both Azure credentials and connection strings.
/// Enables local development with Cosmos DB emulator via connection string, and private-account
/// access through a Bastion SOCKS5 proxy (Gateway mode) via COSMOS_SOCKS5_PROXY.
/// </summary>
public static class CosmosClientFactory
{
    private static CosmosClientOptions BuildClientOptions(
        IConfiguration configuration,
        ILogger logger,
        bool useGatewayMode,
        string? socks5ProxyUrl)
    {
        var options = new CosmosClientOptions
        {
            ApplicationName = "AzureCosmosDBMCP",
            EnableContentResponseOnWrite = false,
            RequestTimeout = TimeSpan.FromSeconds(60)
        };

        if (useGatewayMode)
        {
            // Emulator/local and Bastion SOCKS paths are HTTPS gateway (port 443).
            options.ConnectionMode = ConnectionMode.Gateway;
        }
        else if (!string.IsNullOrWhiteSpace(socks5ProxyUrl))
        {
            // Direct mode opens replica TCP ports and cannot use SOCKS5 — fail closed.
            throw new InvalidOperationException(
                "COSMOS_SOCKS5_PROXY (or socks5 ALL_PROXY) requires Gateway connection mode. " +
                "Set COSMOS_CONNECTION_MODE=Gateway or omit Direct. " +
                "See https://github.com/AzureCosmosDB/MCPToolKit/issues/152");
        }

        string? socksHost = null;
        var socksPort = 0;
        if (!string.IsNullOrWhiteSpace(socks5ProxyUrl))
        {
            if (!Socks5Connect.TryParseProxyUrl(socks5ProxyUrl, out socksHost!, out socksPort, out var parseError))
            {
                throw new InvalidOperationException(parseError ?? "Invalid COSMOS_SOCKS5_PROXY value.");
            }
            logger.LogInformation(
                "Cosmos Gateway traffic will dial via SOCKS5 proxy {ProxyHost}:{ProxyPort}",
                socksHost, socksPort);
        }

        var sslVerifySetting = configuration["COSMOS_EMULATOR_SSL_VERIFY"]
            ?? Environment.GetEnvironmentVariable("COSMOS_EMULATOR_SSL_VERIFY");
        var disableSslVerify = !string.IsNullOrWhiteSpace(sslVerifySetting)
            && bool.TryParse(sslVerifySetting, out var sslVerify)
            && !sslVerify;

        if (disableSslVerify)
        {
            logger.LogWarning("COSMOS_EMULATOR_SSL_VERIFY=false detected. TLS certificate validation is disabled for Cosmos DB emulator connections.");
        }

        if (socksHost is not null || disableSslVerify)
        {
            var proxyHost = socksHost;
            var proxyPort = socksPort;
            options.HttpClientFactory = () =>
            {
                if (proxyHost is not null)
                {
                    var handler = new SocketsHttpHandler
                    {
                        ConnectCallback = async (context, cancellationToken) =>
                        {
                            var ep = context.DnsEndPoint;
                            return await Socks5Connect.ConnectAsync(
                                proxyHost, proxyPort, ep.Host, ep.Port, cancellationToken).ConfigureAwait(false);
                        }
                    };
                    if (disableSslVerify)
                    {
                        handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
                    }
                    return new HttpClient(handler);
                }

                return new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            };
        }

        return options;
    }

    /// <summary>
    /// Create a CosmosClient with fallback support for connection strings and Azure credentials.
    /// 
    /// Priority order:
    /// 1. COSMOS_CONNECTION_STRING - for emulator or local development
    /// 2. COSMOS_ENDPOINT with DefaultAzureCredential - for cloud production
    ///
    /// Optional Bastion / private-network path:
    /// - COSMOS_SOCKS5_PROXY=socks5://127.0.0.1:&lt;port&gt; (or ALL_PROXY=socks5://…)
    /// - Forces ConnectionMode.Gateway and dials HTTPS through SOCKS5
    /// - Optional COSMOS_CONNECTION_MODE=Gateway|Direct
    /// </summary>
    public static CosmosClient CreateCosmosClient(IConfiguration configuration, ILogger logger)
    {
        var socks5Proxy = ResolveSocks5Proxy(configuration);
        var connectionMode = ResolveConnectionMode(configuration, socks5Proxy);

        // Check for connection string first (emulator/local development)
        var connectionString = configuration["COSMOS_CONNECTION_STRING"]
            ?? Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogInformation("Creating CosmosClient using connection string (emulator/local mode)");
            return new CosmosClient(
                connectionString,
                BuildClientOptions(configuration, logger, useGatewayMode: true, socks5Proxy));
        }

        // Fall back to Azure credentials for cloud production
        var endpoint = configuration["COSMOS_ENDPOINT"]
            ?? Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException(
                "Either COSMOS_ENDPOINT or COSMOS_CONNECTION_STRING environment variable must be set. " +
                "For emulator/local development, use COSMOS_CONNECTION_STRING. " +
                "For cloud production, use COSMOS_ENDPOINT with Azure credentials.");
        }

        var useGateway = connectionMode != ConnectionMode.Direct;
        logger.LogInformation(
            "Creating CosmosClient using Azure credentials (cloud mode, ConnectionMode={Mode})",
            useGateway ? "Gateway" : "Direct");
        var credential = new DefaultAzureCredential();

        return new CosmosClient(
            endpoint,
            credential,
            BuildClientOptions(configuration, logger, useGatewayMode: useGateway, socks5Proxy));
    }

    public static string? ResolveSocks5Proxy(IConfiguration configuration)
    {
        var explicitProxy = configuration["COSMOS_SOCKS5_PROXY"]
            ?? Environment.GetEnvironmentVariable("COSMOS_SOCKS5_PROXY");
        if (!string.IsNullOrWhiteSpace(explicitProxy))
        {
            return explicitProxy.Trim();
        }

        var allProxy = configuration["ALL_PROXY"]
            ?? Environment.GetEnvironmentVariable("ALL_PROXY")
            ?? Environment.GetEnvironmentVariable("all_proxy");
        if (!string.IsNullOrWhiteSpace(allProxy)
            && allProxy.Contains("socks5", StringComparison.OrdinalIgnoreCase))
        {
            return allProxy.Trim();
        }

        return null;
    }

    /// <summary>
    /// Default: Direct for cloud (historical). SOCKS proxy or explicit Gateway forces Gateway.
    /// </summary>
    public static ConnectionMode ResolveConnectionMode(IConfiguration configuration, string? socks5Proxy)
    {
        var raw = configuration["COSMOS_CONNECTION_MODE"]
            ?? Environment.GetEnvironmentVariable("COSMOS_CONNECTION_MODE");

        if (!string.IsNullOrWhiteSpace(socks5Proxy))
        {
            if (!string.IsNullOrWhiteSpace(raw)
                && raw.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "COSMOS_CONNECTION_MODE=Direct is incompatible with COSMOS_SOCKS5_PROXY / socks5 ALL_PROXY.");
            }
            return ConnectionMode.Gateway;
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (raw.Equals("Gateway", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionMode.Gateway;
            }
            if (raw.Equals("Direct", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionMode.Direct;
            }
            throw new InvalidOperationException(
                $"COSMOS_CONNECTION_MODE must be Gateway or Direct (got '{raw}').");
        }

        return ConnectionMode.Direct;
    }
}

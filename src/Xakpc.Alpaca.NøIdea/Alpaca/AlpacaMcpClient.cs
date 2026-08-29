using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Xakpc.Alpaca.NøIdea.Alpaca;

/// <summary>Where the read-only Alpaca MCP server is, and how to reach it.</summary>
/// <remarks>
/// A URL selects the development HTTP transport. A command selects the deployed stdio
/// transport. Exactly one must be set (ADR-012).
/// </remarks>
public sealed record AlpacaMcpOptions(string? ReadOnlyUrl, string? ServerCommand, string? ReadOnlyToolsets)
{
    public const string ReadOnlyToolsetsDefault = "assets,stock-data,options-data,news,corporate-actions";

    public static AlpacaMcpOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("Alpaca__Mcp__ReadOnlyUrl"),
        Environment.GetEnvironmentVariable("Alpaca__Mcp__ServerCommand"),
        Environment.GetEnvironmentVariable("Alpaca__Mcp__ReadOnlyToolsets") ?? ReadOnlyToolsetsDefault);
}

/// <summary>
/// The single read-only MCP connection. It exists for the LLM agents.
/// </summary>
/// <remarks>
/// There is no trading MCP connection. Deterministic C# uses
/// <see cref="AlpacaClients"/>, so no MCP server this host runs holds an order tool.
/// That removes the toolset split as a thing that can be misconfigured.
/// </remarks>
public static class AlpacaMcpClient
{
    /// <summary>
    /// Connects, discovers the tools, and fails if any of them can change the account.
    /// </summary>
    public static async Task<(McpClient Client, IReadOnlyList<McpClientTool> ApprovedTools)> ConnectAsync(
        AlpacaMcpOptions options,
        AlpacaOptions credentials,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        var logger = loggerFactory.CreateLogger(typeof(AlpacaMcpClient));
        var transport = CreateTransport(options, credentials);

        var client = await McpClient.CreateAsync(
            transport, clientOptions: null, loggerFactory, cancellationToken);

        // The server reports two different version numbers. Log both: the Python
        // package version and serverInfo.version do not agree, and knowing which one
        // moved is what makes a surprise upgrade diagnosable.
        logger.LogInformation(
            "Alpaca MCP connected. serverInfo: {ServerName} {ServerVersion}. Toolsets: {Toolsets}.",
            client.ServerInfo?.Name, client.ServerInfo?.Version, options.ReadOnlyToolsets);

        var discovered = await client.ListToolsAsync(cancellationToken: cancellationToken);

        // Log the full discovered set before asserting. When the assertion trips, the
        // first question is always "what did the server actually expose", and a
        // stack trace without the list cannot answer it.
        logger.LogInformation(
            "MCP server exposed {Count} tools: {Names}.",
            discovered.Count, string.Join(", ", discovered.Select(t => t.Name).Order(StringComparer.Ordinal)));

        McpToolCatalog.AssertNoForbiddenTool(discovered);

        var approved = discovered.Where(McpToolCatalog.IsApprovedResearchTool).ToArray();

        logger.LogInformation(
            "MCP tools discovered: {Discovered}. Approved for agents: {Approved} ({Names}).",
            discovered.Count, approved.Length, string.Join(", ", approved.Select(t => t.Name)));

        return (client, approved);
    }

    private static IClientTransport CreateTransport(AlpacaMcpOptions options, AlpacaOptions credentials)
    {
        var hasUrl = !string.IsNullOrWhiteSpace(options.ReadOnlyUrl);
        var hasCommand = !string.IsNullOrWhiteSpace(options.ServerCommand);

        if (hasUrl == hasCommand)
        {
            throw new InvalidOperationException(
                "Set exactly one of Alpaca__Mcp__ReadOnlyUrl (development) or "
                + "Alpaca__Mcp__ServerCommand (deployed). Both or neither is a configuration error.");
        }

        if (hasUrl)
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "alpaca-mcp-readonly",
                Endpoint = new Uri(options.ReadOnlyUrl!),
            });
        }

        // ArgumentList only, never a command string, and never a shell.
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "alpaca-mcp-readonly",
            Command = options.ServerCommand!,
            Arguments = ["--transport", "stdio"],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["ALPACA_API_KEY"] = credentials.ApiKey,
                ["ALPACA_SECRET_KEY"] = credentials.SecretKey,
                ["ALPACA_PAPER_TRADE"] = "true",
                ["ALPACA_TOOLSETS"] = options.ReadOnlyToolsets,
            },
        });
    }
}

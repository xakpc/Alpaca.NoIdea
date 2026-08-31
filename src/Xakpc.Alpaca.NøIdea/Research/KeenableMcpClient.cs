using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Xakpc.Alpaca.NøIdea.Research;

/// <summary>
/// The web-research MCP connection. Search and page fetch, for the seats that read text.
/// </summary>
/// <remarks>
/// <para>
/// This replaced a provider-hosted web-search tool. A hosted tool is not portable: Anthropic
/// maps it to a <c>web_search</c> server tool, OpenAI's chat-completions endpoint answers
/// <c>Unknown parameter: 'web_search_options'</c> and returns 400, and a rejected call is an
/// abstention. **An MCP tool is an ordinary function call and behaves the same on all three
/// providers**, so the room reads the web the same way whichever model holds the seat.
/// </para>
/// <para>
/// The tools are read-only and the content they return is <b>untrusted</b>. Prompts say so,
/// and <c>RiskGuard</c> bounds what a hostile page could ever cause (ADR-017).
/// </para>
/// </remarks>
public static class KeenableMcpClient
{
    public const string KeyVariable = "KEENABLE_API_KEY";

    private static readonly Uri Endpoint = new("https://api.keenable.ai/mcp");

    /// <summary>
    /// The tools a seat may hold.
    /// </summary>
    /// <remarks>
    /// An allowlist, not whatever the server offers. The same rule the Alpaca connection
    /// follows (ADR-005, ADR-006): a server that grows a tool later must not hand it to a
    /// model because nobody looked.
    /// </remarks>
    public static readonly IReadOnlySet<string> ApprovedTools =
        new HashSet<string>(StringComparer.Ordinal) { "search_web_pages", "fetch_page_content" };

    /// <summary>Connects and returns the approved research tools, or nothing when there is no key.</summary>
    public static async Task<(McpClient? Client, IReadOnlyList<McpClientTool> ApprovedResearchTools)> ConnectAsync(
        string? apiKey,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(typeof(KeenableMcpClient));

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Not fatal. Reading text is the alpha hypothesis (ADR-013), so losing it matters,
            // but a run that decides from Alpaca data alone is still a correct run.
            logger.LogWarning(
                "{Variable} is not set, so the seats get no web research. Add it to .env.",
                KeyVariable);
            return (null, []);
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "keenable",
            Endpoint = Endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["X-API-Key"] = apiKey },
        });

        var client = await McpClient.CreateAsync(
            transport, clientOptions: null, loggerFactory, cancellationToken);

        var discovered = await client.ListToolsAsync(cancellationToken: cancellationToken);

        // Log what the server offered before filtering. When the approved list comes back
        // short, the first question is what the server actually exposed.
        logger.LogInformation(
            "Keenable connected: {ServerName} {ServerVersion}. Exposed {Count} tools: {Names}.",
            client.ServerInfo?.Name, client.ServerInfo?.Version, discovered.Count,
            string.Join(", ", discovered.Select(tool => tool.Name).Order(StringComparer.Ordinal)));

        var approved = discovered.Where(tool => ApprovedTools.Contains(tool.Name)).ToArray();

        foreach (var name in ApprovedTools.Where(
            name => !approved.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal))))
        {
            logger.LogWarning("Keenable did not expose the approved tool {Name}.", name);
        }

        return (client, approved);
    }
}

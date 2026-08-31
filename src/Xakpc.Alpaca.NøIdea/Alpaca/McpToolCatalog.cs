using ModelContextProtocol.Client;

namespace Xakpc.Alpaca.NøIdea.Alpaca;

/// <summary>
/// The allowlist that filters discovered MCP tools before an LLM agent receives them,
/// and the assertion that stops the process if the connection ever exposes a tool that
/// can change the account.
/// </summary>
/// <remarks>
/// <para>
/// Two independent controls. The first is the server-side <c>ALPACA_TOOLSETS</c> value,
/// which starts the server without the trading tools at all. This is the second: if
/// that one is widened by a configuration mistake or a server upgrade,
/// <see cref="AssertNoForbiddenTool"/> fails startup rather than letting an agent see
/// an order tool.
/// </para>
/// <para>
/// Deterministic C# reaches Alpaca through the typed SDK, not through MCP, so no MCP
/// server this host runs is expected to hold a write tool at all. This class exists to
/// prove that at startup instead of assuming it.
/// </para>
/// </remarks>
public static class McpToolCatalog
{
    /// <summary>
    /// Name segments that mark a tool an LLM must never receive. Matching is on
    /// underscore-separated segments, not on substrings: a substring test for
    /// <c>trade</c> would wrongly reject <c>get_stock_trades</c>, which is read-only
    /// market data, and a substring test for <c>order</c> would wrongly reject
    /// <c>get_crypto_orderbook</c>.
    /// </summary>
    private static readonly HashSet<string> ForbiddenSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "account", "accounts",
        "position", "positions",
        "order", "orders",
        "exercise", "liquidate",
    };

    /// <summary>
    /// Verbs that change state. A tool whose name starts with one of these is a write
    /// tool whatever noun follows it.
    /// </summary>
    private static readonly string[] MutatingPrefixes =
    [
        "place_", "submit_", "create_", "post_", "open_",
        "cancel_", "replace_", "update_", "patch_", "modify_",
        "close_", "delete_", "remove_", "exercise_", "liquidate_",
        "buy_", "sell_",
    ];

    /// <summary>
    /// The tools the agents may use. Exact names, not prefixes: ADR-011 pins the server
    /// commit, so the names are stable, and an exact list cannot silently widen when a
    /// new tool appears. A new tool must be reviewed and added deliberately.
    /// </summary>
    /// <remarks>
    /// Crypto tools are excluded because this system trades equity options. The Alpaca
    /// documentation and API-spec search tools are excluded because they answer
    /// questions about Alpaca rather than about the market, and every tool in the list
    /// costs tokens and adds a way to choose wrongly.
    /// </remarks>
    private static readonly HashSet<string> ApprovedTools = new(StringComparer.Ordinal)
    {
        // Stock market data.
        "get_stock_bars", "get_stock_latest_bar", "get_stock_latest_quote",
        "get_stock_latest_trade", "get_stock_quotes", "get_stock_snapshot",
        "get_stock_trades",

        // Option market data, including greeks through the snapshot.
        "get_option_bars", "get_option_contract",
        "get_option_latest_quote", "get_option_latest_trade",
        "get_option_snapshot", "get_option_trades",

        // News. The remaining alpha hypothesis rests on the agents reading text.
        "get_news",

        // Reference data: assets, the market calendar, and the clock.
        "get_asset", "get_all_assets", "get_calendar", "get_clock",

        // Corporate actions, which move a price for reasons the news may not carry.
        "get_corporate_actions", "get_corporate_action_announcement",
        "get_corporate_action_announcements",

        // Market context.
        "get_market_movers", "get_most_active_stocks",
    };

    /// <summary>True when an LLM agent may receive this tool.</summary>
    public static bool IsApprovedResearchTool(McpClientTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return IsApprovedResearchTool(tool.Name);
    }

    /// <summary>
    /// True when an LLM agent may receive the tool with this name. The name overload
    /// exists so the policy can be tested without constructing a live MCP tool.
    /// </summary>
    public static bool IsApprovedResearchTool(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return !IsForbidden(toolName) && ApprovedTools.Contains(toolName);
    }

    /// <summary>True when no LLM may receive this tool under any circumstance.</summary>
    public static bool IsForbidden(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        if (MutatingPrefixes.Any(prefix => toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return toolName
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Any(ForbiddenSegments.Contains);
    }

    /// <summary>
    /// Fails the process when the read-only connection exposes a tool that can change
    /// the account or read account state. A misconfigured toolset must stop the system,
    /// not degrade it quietly.
    /// </summary>
    /// <exception cref="InvalidOperationException">A forbidden tool is present.</exception>
    public static void AssertNoForbiddenTool(IEnumerable<McpClientTool> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        var forbidden = discovered
            .Select(tool => tool.Name)
            .Where(IsForbidden)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (forbidden.Length != 0)
        {
            throw new InvalidOperationException(
                "The read-only MCP connection exposes tools that can reach the account: "
                + string.Join(", ", forbidden)
                + ". Check ALPACA_TOOLSETS on the read-only server.");
        }
    }
}

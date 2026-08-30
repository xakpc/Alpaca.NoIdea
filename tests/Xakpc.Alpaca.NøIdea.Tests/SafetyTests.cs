using Alpaca.Markets;
using Xakpc.Alpaca.NøIdea.Alpaca;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The guardrails that must not regress. Each test here corresponds to a rule in
/// .lode/trading/risk-guardrails.md or .lode/llm/tool-policy.md.
/// </summary>
public class SafetyTests
{
    [Fact]
    public void TheEnvironmentIsPaperAndCannotBeConfigured()
    {
        // The paper guarantee is compile-time. If this test fails, someone made the
        // environment configurable, and no runtime check replaces what was lost.
        Assert.Same(Environments.Paper, AlpacaClients.Environment);
        Assert.NotSame(Environments.Live, AlpacaClients.Environment);
    }

    [Theory]
    // Write tools. These are what the toolset split is supposed to keep away.
    [InlineData("place_option_order")]
    [InlineData("place_stock_order")]
    [InlineData("place_crypto_order")]
    [InlineData("cancel_order_by_id")]
    [InlineData("cancel_all_orders")]
    [InlineData("replace_order_by_id")]
    [InlineData("close_position")]
    [InlineData("close_all_positions")]
    [InlineData("exercise_options_position")]
    [InlineData("do_not_exercise_options_position")]
    [InlineData("update_account_config")]
    // Account-state reads. An agent has no reason to see the balance, and letting it
    // would invite reasoning about position sizing, which is C# territory.
    [InlineData("get_account_info")]
    [InlineData("get_account_activities")]
    [InlineData("get_all_positions")]
    [InlineData("get_open_position")]
    [InlineData("get_orders")]
    [InlineData("get_order_by_client_id")]
    public void ForbiddenToolsAreRejected(string toolName)
    {
        Assert.True(McpToolCatalog.IsForbidden(toolName), $"{toolName} must be forbidden.");
    }

    [Theory]
    // Read-only market data whose names contain words that a careless substring test
    // would flag. These are the false positives that made the first catalog wrong.
    [InlineData("get_stock_trades")]
    [InlineData("get_stock_latest_trade")]
    [InlineData("get_option_trades")]
    [InlineData("get_option_latest_trade")]
    [InlineData("get_crypto_orderbook")]
    public void ReadOnlyMarketDataIsNotMistakenForAWriteTool(string toolName)
    {
        Assert.False(McpToolCatalog.IsForbidden(toolName), $"{toolName} is read-only market data.");
    }

    [Fact]
    public void AnUnknownToolIsNotApprovedEvenWhenItLooksHarmless()
    {
        // A tool that appears after a server upgrade must not reach an agent just
        // because its name contains no forbidden word. The allowlist is explicit, so
        // "not forbidden" and "approved" are different answers.
        Assert.False(McpToolCatalog.IsForbidden("get_some_new_tool"));
        Assert.False(McpToolCatalog.IsApprovedResearchTool("get_some_new_tool"));
    }

    [Theory]
    [InlineData("get_stock_bars")]
    [InlineData("get_option_chain")]
    [InlineData("get_option_snapshot")]
    [InlineData("get_news")]
    [InlineData("get_clock")]
    public void TheResearchToolsTheAgentsNeedAreApproved(string toolName)
    {
        Assert.True(McpToolCatalog.IsApprovedResearchTool(toolName));
    }

    [Fact]
    public void NoForbiddenToolCanEverBeApproved()
    {
        // The two rules must not disagree: anything forbidden is unreachable through
        // the approval path, whatever the allowlist happens to contain.
        string[] writeTools =
        [
            "place_option_order", "cancel_all_orders", "close_position",
            "exercise_options_position", "get_account_info", "get_all_positions",
        ];

        Assert.All(writeTools, tool => Assert.False(McpToolCatalog.IsApprovedResearchTool(tool)));
    }
}

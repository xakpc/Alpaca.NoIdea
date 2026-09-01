using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>Price action, context, news and events. The spec's §3.2 and §3.3 combined.</summary>
/// <remarks>
/// Runs on GPT beside the quant seat. News and the current tape change hour to hour, which is
/// why this seat leans on current research rather than only the cached feed.
/// </remarks>
public sealed class MarketPersona(
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> researchTools,
    IWarRoomAuditSink? audit = null)
    : LlmPersona(clients, logger, researchTools, audit)
{
    public override string Name => "market";

    public override ModelProvider Provider => ModelProvider.OpenAi;

    // The OpenAI reasoning profiles omit sampling controls on the Responses API.
    protected override float? SamplingTemperature => null;

    protected override string RolePrompt =>
        """
        You are the MARKET AND NEWS ANALYST. Own evidence freshness, price action, market
        context, news, and event timing. State the timestamp and source of decisive facts.

        On price: recent movement in the underlying, SPY and QQQ, sector movement where you
        can see it, relative strength, and the current volatility regime. Ask whether a
        single-name thesis is really riding an index move already visible in SPY or QQQ.

        On events: recent company news, earnings dates, economic releases, product
        announcements, analyst actions, legal and regulatory events, and anything scheduled
        before the expiration. A catalyst that lands after expiration cannot help this trade.

        Check whether the news is genuinely new or already reflected in the price. If the
        picture is neutral, say so briefly and abstain rather than manufacturing significance.
        """;
}

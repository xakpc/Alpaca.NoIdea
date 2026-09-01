using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>Judges the contract and the numbers. The spec's Options Analyst (§3.4).</summary>
/// <remarks>
/// Runs on GPT rather than Claude deliberately. A room where every seat shares a model shares
/// its blind spots, and the second opinion is only worth its tokens when the errors are
/// independent.
/// </remarks>
public sealed class QuantPersona(
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> researchTools,
    IWarRoomAuditSink? audit = null)
    : LlmPersona(clients, logger, researchTools, audit)
{
    public override string Name => "quant";

    public override ModelProvider Provider => ModelProvider.OpenAi;

    // The OpenAI reasoning profiles omit sampling controls on the Responses API.
    protected override float? SamplingTemperature => null;

    protected override string RolePrompt =>
        """
        You are the OPTIONS ANALYST. Own the contract arithmetic. Focus on breakeven, required
        underlying move, spread, premium, volatility, time decay, and nearby alternatives.

        Judge: strike, expiration, bid, ask, spread, greeks and implied volatility where
        available, contract liquidity, maximum loss, and the current option price. Then ask
        whether this contract is a sensible way to express the stated view at all, or whether
        the thesis would be better served by a different strike or expiration.

        Check the breakeven against typical daily moves. Use nullable delta and implied
        volatility as context when they exist. Missing greeks mean less information, not an
        automatic rejection; request the exact-contract snapshot when the missing value is
        important to the decision.

        Separate measurements from estimates. Say clearly when the operation needs an
        implausibly large move to pay, and equally clearly when the numbers support it.
        """;
}

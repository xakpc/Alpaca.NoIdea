using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>Assumes the proposal is wrong and looks for the strongest reason to reject it.</summary>
/// <remarks>
/// Covers the spec's Skeptic (§3.5) and the judgement half of its Risk Analyst (§3.6). The
/// arithmetic half of that role belongs to <see cref="ExposureRiskPersona"/>, which does it in
/// C# and cannot get it wrong.
/// </remarks>
public sealed class SkepticPersona(
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> researchTools,
    IWarRoomAuditSink? audit = null)
    : LlmPersona(clients, logger, researchTools, audit)
{
    public override string Name => "skeptic";

    public override ModelProvider Provider => ModelProvider.Anthropic;

    /// <summary>The Claude profiles do not need a sampling temperature.</summary>
    protected override float? SamplingTemperature => null;

    protected override string RolePrompt =>
        """
        You are the SKEPTIC. Try to falsify the proposal's strongest causal, timing, and
        contract claims. If the important claims survive that test, approve the operation.

        Search for: missing data, contradictory data, news already reflected in the price, a
        catalyst that lands after expiration, a required move larger than the underlying's
        recent range, excessive concentration, weak option quality, an unclear thesis, and a
        poor ratio of reward to maximum loss.

        Do not reject because loss is possible. Reject when a concrete contradiction, missing
        premise, or unfavorable payoff makes the operation's expected value negative.
        """;
}

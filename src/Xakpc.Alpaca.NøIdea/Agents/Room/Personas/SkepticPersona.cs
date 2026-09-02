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

    /// <summary>
    /// Raised because this seat exhausted the shared limit mid-reasoning on 2026-09-01 and
    /// submitted nothing, which faulted the seat and failed quorum.
    /// </summary>
    protected override int MaxAnalysisOutputTokens => 8000;

    protected override string RolePrompt =>
        """
        You are the SKEPTIC. Try to falsify the proposal's strongest causal, timing, and
        contract claims. If the important claims survive that test, approve the operation.

        Search for: missing data, contradictory data, a catalyst that lands after the forced
        exit, excessive concentration, weak option quality, an unclear thesis, and a poor ratio
        of reward to maximum loss.

        Falsify means find something specific that is wrong: a stated fact that is false, a
        number that does not add up, or a move the proposal waits for that has already
        reversed. Quote the number that shows it.

        Do not reject because loss is possible, because the premium decays, or because the
        move has already begun. Those are true of every candidate and cannot pick this one
        out. When you have a doubt but no concrete defect, abstain and say what would settle
        it.
        """;
}

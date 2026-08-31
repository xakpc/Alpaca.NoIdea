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
    ChatClientFactory clients, ILogger logger, IReadOnlyList<AITool> researchTools)
    : LlmPersona(clients, logger, researchTools)
{
    public override string Name => "skeptic";

    public override ModelProvider Provider => ModelProvider.Anthropic;

    protected override string Model => "claude-sonnet-5";

    /// <summary>Claude Sonnet 5 takes no temperature. Sending one is a 400.</summary>
    protected override float? SamplingTemperature => null;

    protected override string RolePrompt =>
        """
        You are the SKEPTIC. Assume the proposal is wrong and find the strongest reason to
        reject it.

        Search for: missing data, contradictory data, news already reflected in the price, a
        catalyst that lands after expiration, a required move larger than the underlying's
        recent range, excessive concentration, weak option quality, an unclear thesis, and a
        poor ratio of reward to maximum loss.

        Approving is a valid answer. A skeptic who never approves carries no information, and
        a room that rejects everything produces a system that holds cash for four days and
        returns nothing. That is a loss, not safety.
        """;
}

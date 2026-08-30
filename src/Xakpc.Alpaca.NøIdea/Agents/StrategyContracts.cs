using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents;

/// <summary>What the agent is allowed to ask for.</summary>
/// <remarks>
/// A closed set, on purpose. The agent picks from these; it never emits code, SQL, a shell
/// command, or a broker call. Anything outside this enum cannot be expressed, so it cannot be
/// executed (ADR-005).
/// </remarks>
public enum StrategyActionKind
{
    /// <summary>Do nothing this cycle. Always a valid answer.</summary>
    Hold = 0,

    /// <summary>Buy a long call on <see cref="StrategyAction.ContractSymbol"/>.</summary>
    OpenCall = 1,

    /// <summary>Buy a long put on <see cref="StrategyAction.ContractSymbol"/>.</summary>
    OpenPut = 2,

    /// <summary>Close an open position completely.</summary>
    ClosePosition = 3,
}

/// <summary>One thing the agent wants to do.</summary>
/// <remarks>
/// Every field is data. The <see cref="ContractSymbol"/> must name a contract the harness put
/// in front of the agent this cycle; a symbol the agent invents is rejected rather than sent,
/// which is what stops a hallucinated contract from reaching the broker.
/// </remarks>
public sealed record StrategyAction
{
    public required StrategyActionKind Kind { get; init; }

    /// <summary>Required for every kind except <see cref="StrategyActionKind.Hold"/>.</summary>
    public string? ContractSymbol { get; init; }

    public int Contracts { get; init; } = 1;

    /// <summary>
    /// The agent's own probability that this trade works out. Recorded so the agent can be
    /// scored with a Brier score later, which is the only honest way to learn whether it has
    /// an edge.
    /// </summary>
    public decimal? Probability { get; init; }

    /// <summary>Why. Written to the audit trail whether the action runs or is rejected.</summary>
    public required string Reasoning { get; init; }

    public static StrategyAction Hold(string reasoning) =>
        new() { Kind = StrategyActionKind.Hold, Reasoning = reasoning };
}

/// <summary>Everything the agent decided this cycle.</summary>
public sealed record StrategyDecision
{
    public IReadOnlyList<StrategyAction> Actions { get; init; } = [];

    /// <summary>
    /// A rewritten policy, or null to keep the current one. The harness clamps it to
    /// <see cref="RiskOptions"/> before use.
    /// </summary>
    public StrategyPolicy? RevisedPolicy { get; init; }

    public static StrategyDecision Nothing(string reasoning) =>
        new() { Actions = [StrategyAction.Hold(reasoning)] };
}

/// <summary>One contract the harness is offering the agent this cycle.</summary>
/// <remarks>
/// The harness has already applied the cheap filter, so everything here passed contract
/// quality, the policy expiration window, and the tradeable probability band. The agent
/// chooses among these; it does not go looking for others.
/// </remarks>
public sealed record CandidateView
{
    public required OptionCandidate Candidate { get; init; }
    public required decimal UnderlyingPrice { get; init; }

    /// <summary>From the option ladder. Risk-neutral, so slightly below the real-world chance.</summary>
    public decimal? MarketProbability { get; init; }

    public int RecentNewsCount { get; init; }

    /// <summary>The premium for one contract, in dollars.</summary>
    public decimal CostPerContract => Candidate.ReferencePrice * 100m;
}

/// <summary>What the agent sees before it decides.</summary>
public sealed record StrategyContext
{
    public required DateTimeOffset NowUtc { get; init; }
    public required AccountState Account { get; init; }
    public required IReadOnlyList<PositionState> Positions { get; init; }
    public required IReadOnlyList<CandidateView> Candidates { get; init; }
    public required StrategyPolicy Policy { get; init; }

    /// <summary>Headlines for the tracked symbols, newest first.</summary>
    public IReadOnlyList<NewsItem> News { get; init; } = [];

    /// <summary>
    /// What the agent did before, and how it turned out. This is what makes the strategy
    /// adaptive without the agent writing any code: it revises the policy from its own
    /// measured results.
    /// </summary>
    public IReadOnlyList<PastOutcome> RecentOutcomes { get; init; } = [];

    /// <summary>How many positions may still be opened, after the hard limits.</summary>
    public required int RemainingPositionSlots { get; init; }

    /// <summary>True when the daily loss circuit breaker has fired. No new positions.</summary>
    public required bool NewPositionsHalted { get; init; }
}

/// <summary>One closed trade, for the agent to learn from.</summary>
public sealed record PastOutcome(
    DateTimeOffset OpenedUtc,
    string ContractSymbol,
    decimal EntryPrice,
    decimal? ExitPrice,
    decimal? RealizedPnl,
    string? Reasoning);

/// <summary>
/// The thing that decides what to trade. One implementation asks an LLM; one returns canned
/// answers so the loop can be tested with no token spend.
/// </summary>
/// <remarks>
/// An agent cannot submit an order. It returns a <see cref="StrategyDecision"/> and the
/// harness decides what, if anything, to execute. Every action still passes
/// <c>RiskGuard</c> afterwards, so a compromised or malfunctioning agent cannot exceed a
/// single hard limit.
/// </remarks>
public interface IStrategyAgent
{
    /// <summary>A short name for the audit trail.</summary>
    string Name { get; }

    Task<StrategyDecision> DecideAsync(StrategyContext context, CancellationToken cancellationToken);
}

/// <summary>
/// An agent that can also judge an open position.
/// </summary>
/// <remarks>
/// Separate from <see cref="IStrategyAgent"/> so a stub or a replay agent is not obliged to
/// implement a review path it has no use for. The loop checks for this and falls back to the
/// deterministic exits when an agent does not offer it, which means a position is never left
/// unguarded by the absence of a reviewer.
/// </remarks>
public interface IPositionReviewer
{
    /// <summary>
    /// Judges one open position. The answer may be to hold, which is what
    /// <see cref="StrategyDecision.Nothing"/> means here.
    /// </summary>
    Task<StrategyDecision> ReviewPositionAsync(
        StrategyContext context,
        PositionState position,
        string triggerReason,
        decimal? unrealizedFraction,
        int? daysToExpiration,
        CancellationToken cancellationToken);
}

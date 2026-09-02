using System.Text.Json.Serialization;
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
    /// For an opening trade, the agent's probability of positive realized P&amp;L at exit.
    /// A close leaves this null because the unchosen hold result is not observed.
    /// </summary>
    [JsonPropertyName("probability")]
    public decimal? ProfitProbability { get; init; }

    /// <summary>Why. Written to the audit trail whether the action runs or is rejected.</summary>
    public required string Reasoning { get; init; }

    /// <summary>The market figures the proposer says it reasoned from. Checked, not trusted.</summary>
    public MarketClaims? Claims { get; init; }

    public static StrategyAction Hold(string reasoning) =>
        new() { Kind = StrategyActionKind.Hold, Reasoning = reasoning };
}

/// <summary>
/// The numbers a proposer states it used, so they can be compared with the catalog.
/// </summary>
/// <remarks>
/// <para>
/// A model that writes its arithmetic into prose cannot be checked, because prose has no field
/// to compare. Asking for the same figures as data makes a fabricated premise a validation
/// error instead of something a reviewer has to notice.
/// </para>
/// <para>
/// Every field is optional. Making them required would change the submission contract, and a
/// proposal that fails to submit tells us nothing at all. An absent claim is not checked; a
/// present one must match.
/// </para>
/// </remarks>
public sealed record MarketClaims
{
    public decimal? QuotedBid { get; init; }
    public decimal? QuotedAsk { get; init; }
    public decimal? UnderlyingLast { get; init; }
    public decimal? Delta { get; init; }
    public decimal? ImpliedVolatility { get; init; }
}

/// <summary>Why the agent turned down an operation it had already put together.</summary>
/// <remarks>
/// <b>Proposing nothing and rejecting something are different results.</b> Both leave the
/// account unchanged, so both used to arrive as a bare hold and the cycle summary counted
/// neither. That reads as a cycle where nothing happened, when in fact a concrete contract was
/// judged and declined for a stated reason. This record is set only in the second case.
/// </remarks>
public sealed record DecisionRejection
{
    /// <summary>A stable code, for example <c>WITHDRAWN_BY_PROPOSER</c> or <c>ROOM_VOTE</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Which stage declined: <c>room</c> or <c>pre-validation</c>.</summary>
    public required string Stage { get; init; }
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

    /// <summary>
    /// Set when the agent considered a concrete operation and declined it. Null for a plain
    /// hold, which is not a rejection.
    /// </summary>
    public DecisionRejection? Rejection { get; init; }

    public static StrategyDecision Nothing(string reasoning) =>
        new() { Actions = [StrategyAction.Hold(reasoning)] };

    /// <summary>
    /// A rejection that keeps the judged contract, so the audit can score it later.
    /// </summary>
    /// <remarks>
    /// The action stays a hold — nothing is submitted — but it carries the symbol and the
    /// probability the operation was judged on. Without those the decision row records that
    /// something was declined and nothing about what, which cannot support a Brier score or a
    /// counterfactual.
    /// </remarks>
    public static StrategyDecision Declined(
        string reasoning,
        DecisionRejection rejection,
        string? contractSymbol,
        decimal? profitProbability) =>
        new()
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = StrategyActionKind.Hold,
                    ContractSymbol = contractSymbol,
                    ProfitProbability = profitProbability,
                    Reasoning = reasoning,
                },
            ],
            Rejection = rejection,
        };
}

/// <summary>One mechanically tradeable contract in the authoritative catalog.</summary>
/// <remarks>
/// The harness has already applied the mechanical filter, so everything here passed quote,
/// expiration, liquidity, duplicate-position, and one-contract risk checks. The catalog has
/// no probability band or quality rank. The agent decides which contract is attractive.
/// </remarks>
public sealed record TradeableContractView
{
    public required OptionCandidate Contract { get; init; }
    public required decimal UnderlyingPrice { get; init; }

    /// <summary>The premium for one contract, in dollars.</summary>
    public decimal CostPerContract => Contract.Ask.GetValueOrDefault() * 100m;
}

public sealed record UnderlyingSnapshot(
    string Symbol,
    decimal Last,
    DateTimeOffset LastAt,
    decimal? Return1D,
    decimal? Return5D);

public sealed record PortfolioCapacity(
    decimal RemainingRisk,
    int FreePositionSlots,
    bool PendingRiskKnown);

/// <param name="PositionsExitAtUtc">
/// When every open position is sold, whatever its expiration. Restates
/// <paramref name="CompetitionFlattenUtc"/> as the horizon a seat values against, because a
/// reviewer that reads the flatten as a deadline scores expiration payoff instead of the exit
/// price and then rejects the whole eligible universe.
/// </param>
/// <param name="HoursToForcedExit">Hours from now until the forced exit.</param>
/// <param name="ExitIsAlwaysPreExpiry">
/// True while no permitted contract can expire before the forced exit, so selling early is the
/// design rather than a defect in one candidate.
/// </param>
public sealed record TradingConstraints(
    int MinDaysToExpiration,
    int MaxDaysToExpiration,
    int MaxContractsPerTrade,
    decimal MaxRiskPerTrade,
    decimal MaxTotalRisk,
    decimal MaxSpreadFraction,
    TimeSpan MaxQuoteAge,
    DateTimeOffset CompetitionFlattenUtc,
    DateTimeOffset PositionsExitAtUtc,
    decimal HoursToForcedExit,
    bool ExitIsAlwaysPreExpiry);

public sealed record PortfolioPositionView
{
    public required PositionState Position { get; init; }
    public string? Underlying { get; init; }
    public string? OptionType { get; init; }
    public decimal? Strike { get; init; }
    public DateOnly? Expiration { get; init; }
    public decimal? UnrealizedPnlFraction { get; init; }
    public decimal PremiumRisk { get; init; }
    public string? OriginalThesis { get; init; }
    public IReadOnlyList<string> OriginalThesisConditions { get; init; } = [];
}

/// <summary>What the agent sees before it decides.</summary>
public sealed record StrategyContext
{
    public required DateTimeOffset NowUtc { get; init; }
    public required AccountState Account { get; init; }
    public required IReadOnlyList<PositionState> Positions { get; init; }
    public required IReadOnlyList<TradeableContractView> ContractCatalog { get; init; }
    public required StrategyPolicy Policy { get; init; }

    public IReadOnlyList<UnderlyingSnapshot> Underlyings { get; init; } = [];

    public IReadOnlyList<PortfolioPositionView> PortfolioPositions { get; init; } = [];

    public IReadOnlyList<OrderState> PendingOrders { get; init; } = [];

    public PortfolioCapacity Capacity { get; init; } = new(0m, 0, true);

    public TradingConstraints? Constraints { get; init; }

    /// <summary>Headlines for the tracked symbols, newest first.</summary>
    public IReadOnlyList<NewsItem> News { get; init; } = [];

    /// <summary>
    /// What the agent did before, and how it turned out. This is what makes the strategy
    /// adaptive without the agent writing any code: it revises the policy from its own
    /// measured results.
    /// </summary>
    public IReadOnlyList<PastOutcome> RecentOutcomes { get; init; } = [];

    /// <summary>What the room already refused, newest first.</summary>
    public IReadOnlyList<RecentRejection> RecentRejections { get; init; } = [];

    /// <summary>How many positions may still be opened, after the hard limits.</summary>
    public required int RemainingPositionSlots { get; init; }

    /// <summary>True when an account-wide risk check prevents new positions.</summary>
    public required bool NewPositionsHalted { get; init; }

    /// <summary>The exact failed risk check when <see cref="NewPositionsHalted"/> is true.</summary>
    public string? NewPositionsHaltReason { get; init; }
}

/// <summary>What one seat thought about the last decision.</summary>
/// <remarks>
/// The audit shape of an opinion, not the room's own. <c>ProfitProbability</c> and
/// <c>Confidence</c> are nullable because a seat argues in words and need not produce a
/// number: the plain-C# exposure seat never does, and requiring one would drop exactly the
/// seats that reason rather than compute.
/// </remarks>
public sealed record SeatOpinion(
    string Seat,
    string? Vote,
    [property: JsonPropertyName("probability")] decimal? ProfitProbability,
    decimal? Confidence,
    string? Reasoning,
    string? Evidence);

/// <summary>
/// An agent that can say who decided, and why, for the audit trail.
/// </summary>
/// <remarks>
/// Optional. An agent with no room implements nothing and the loop records the decision
/// without seat detail. This keeps the loop free of war-room types: it audits opinions, and
/// does not know that a room produced them.
/// </remarks>
public interface IExplainsDecision
{
    /// <summary>An id for the sitting that produced the last decision, or null.</summary>
    string? LastProposalId { get; }

    /// <summary>The confidence-weighted net of the vote, or null when nobody voted.</summary>
    decimal? LastNetVote { get; }

    /// <summary>One entry per seat that took part in the last decision.</summary>
    IReadOnlyList<SeatOpinion> LastOpinions { get; }

    /// <summary>Every immutable proposal version from the last sitting.</summary>
    IReadOnlyList<Room.ProposalReviewPass> LastReviewPasses => [];

    string? LastThesis => null;

    IReadOnlyList<string> LastThesisConditions => [];
}

/// <summary>One operation the room refused, so the next sitting does not repeat it.</summary>
/// <remarks>
/// The room has no memory of its own: each sitting starts from nothing. Without this the
/// proposer re-derives the same thesis every cycle and loses to the same arguments, which is
/// what three consecutive cycles did on 2026-09-01.
/// </remarks>
public sealed record RecentRejection(
    DateTimeOffset AtUtc,
    string? ContractSymbol,
    string? Reason);

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
/// Separate from <see cref="IStrategyAgent"/> so a stub agent is not obliged to
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

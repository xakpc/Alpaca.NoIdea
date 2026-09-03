using Microsoft.Extensions.Logging;
using ToonFormat;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>
/// Presents the war room to the trading loop as an ordinary strategy agent.
/// </summary>
/// <remarks>
/// <para>
/// The loop does not know a room exists. It asks an <see cref="IStrategyAgent"/> what to do
/// and applies <c>RiskGuard</c> to the answer, exactly as it does for the stub. That keeps
/// live trading and testing on one path.
/// </para>
/// <para>
/// This adapter owns the two things the loop should not care about: giving each proposal an
/// identity, and totalling what the room spent.
/// </para>
/// </remarks>
public sealed class WarRoomAgent(
    WarRoomSession session,
    TimeProvider time,
    ILogger logger,
    string mode = "test") : IStrategyAgent, IPositionReviewer, IExplainsDecision
{
    private readonly WarRoomSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly string _mode = mode;
    private readonly TokenLedger _spend = new();

    public string Name => "war-room";

    /// <summary>Everything the room has cost since the process started.</summary>
    public RoomCost TotalCost => _spend.Snapshot();

    /// <summary>The last sitting, for the audit trail.</summary>
    public WarRoomOutcome? LastOutcome { get; private set; }

    public string? LastProposalId => LastOutcome?.ProposalId;

    public decimal? LastNetVote => LastOutcome?.Tally?.Net;

    public IReadOnlyList<ProposalReviewPass> LastReviewPasses => LastOutcome?.ReviewPasses ?? [];

    public string? LastThesis => LastOutcome?.Operation.Thesis;

    public IReadOnlyList<string> LastThesisConditions =>
        LastOutcome?.Operation.ThesisConditions ?? [];

    /// <summary>
    /// The last sitting, flattened to one entry per seat for the audit trail.
    /// </summary>
    /// <remarks>
    /// The independent analysis and the final vote are folded into one row per seat, because
    /// the interesting question afterwards is "what did this seat think, and did it change
    /// its mind" — and both halves of that answer belong on the same row.
    /// </remarks>
    public IReadOnlyList<SeatOpinion> LastOpinions
    {
        get
        {
            if (LastOutcome is not { } outcome)
            {
                return [];
            }

            var seats = outcome.Analyses.Select(analysis => analysis.Persona)
                .Concat(outcome.Votes.Select(vote => vote.Persona))
                .Distinct(StringComparer.Ordinal);

            return
            [
                .. seats.Select(seat =>
                {
                    var analysis = outcome.Analyses.FirstOrDefault(a => a.Persona == seat);
                    var vote = outcome.Votes.FirstOrDefault(v => v.Persona == seat);
                    var said = outcome.Discussion.Where(line => line.Speaker == seat).ToList();

                    return new SeatOpinion(
                        Seat: seat,
                        Vote: vote?.Cast == true ? vote.Vote.ToString() : null,
                        ProfitProbability: vote?.ProfitProbability ?? analysis?.ProfitProbability,
                        Confidence: vote?.Cast == true ? vote.Confidence : analysis?.Confidence,
                        Reasoning: vote?.Rationale ?? analysis?.Analysis,
                        Evidence: Toon.Encode(new
                        {
                            initialVote = analysis?.InitialVote.ToString(),
                            initialConfidence = analysis?.Confidence,
                            analysis = analysis?.Analysis,
                            supportingEvidence = analysis?.SupportingEvidence,
                            risks = analysis?.Risks,
                            said = said.Select(line => new { line.Round, line.Summary }),
                            unresolvedRisk = vote?.UnresolvedRisk,
                            fault = analysis?.Fault ?? vote?.Fault,
                        }));
                }),
            ];
        }
    }

    public async Task<StrategyDecision> DecideAsync(
        StrategyContext context, CancellationToken cancellationToken)
    {
        LastOutcome = null;
        ArgumentNullException.ThrowIfNull(context);
        LastOutcome = null;

        // Spec §14: do not convene the room when nothing may be opened anyway.
        if (context.NewPositionsHalted || context.RemainingPositionSlots <= 0)
        {
            return StrategyDecision.Nothing(
                context.NewPositionsHalted
                    ? context.NewPositionsHaltReason ?? "new positions are halted"
                    : "no free position slot");
        }

        if (context.ContractCatalog.Count == 0)
        {
            return StrategyDecision.Nothing("the tradeable contract catalog is empty");
        }

        var outcome = await _session.RunAsync(
            new WarRoomRequest
            {
                ProposalId = NextProposalId(),
                Mode = _mode,
                Purpose = WarRoomPurpose.NewTrade,
                Market = context,
                AllowedActions = [StrategyActionKind.OpenCall, StrategyActionKind.OpenPut],
            },
            cancellationToken);

        Record(outcome);

        return ToDecision(outcome);
    }

    /// <summary>
    /// Convenes the room over one open position. Spec §11.
    /// </summary>
    /// <remarks>
    /// The same session, the same phases. Only the request differs, which is what stops the
    /// position path and the new-trade path drifting apart.
    /// </remarks>
    public async Task<StrategyDecision> ReviewPositionAsync(
        StrategyContext context,
        PositionState position,
        string triggerReason,
        decimal? unrealizedFraction,
        int? daysToExpiration,
        CancellationToken cancellationToken)
    {
        LastOutcome = null;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(position);
        LastOutcome = null;

        var underReview = new PositionUnderReview
        {
            Position = position,
            TriggerReason = triggerReason,
            UnrealizedPnlFraction = unrealizedFraction,
            DaysToExpiration = daysToExpiration,
            OriginalThesis = context.PortfolioPositions
                .FirstOrDefault(item => string.Equals(
                    item.Position.Symbol, position.Symbol, StringComparison.Ordinal))
                ?.OriginalThesis,
            OriginalThesisConditions = context.PortfolioPositions
                .FirstOrDefault(item => string.Equals(
                    item.Position.Symbol, position.Symbol, StringComparison.Ordinal))
                ?.OriginalThesisConditions ?? [],
        };

        var outcome = await _session.RunAsync(
            new WarRoomRequest
            {
                ProposalId = NextProposalId(),
                Mode = _mode,
                Purpose = WarRoomPurpose.PositionReview,
                Market = context,
                Position = underReview,
                // ADJUST stays disabled until adjustment code is validated (spec §42).
                AllowedActions = [StrategyActionKind.ClosePosition],
            },
            cancellationToken);

        Record(outcome);

        return EscalatedClose(outcome, position) ?? ToDecision(outcome);
    }

    /// <summary>
    /// Turns a room that voted against holding into a close, or null when it did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A review proposal that asks for nothing is a hold, and a rejected hold used to leave the
    /// position open. On 2026-09-02 the room rejected the META hold 0 approve to 3 reject at a
    /// net of -0.53, with two seats writing the exit arithmetic, and no order was sent. The
    /// room's own conclusion had no way to reach the broker.
    /// </para>
    /// <para>
    /// Every condition below is a guard, not a formality. A verdict alone is not enough:
    /// <see cref="WarRoomVerdict.Rejected"/> also carries a proposer withdrawal and, under
    /// <c>RequireEveryVoter</c>, a room where one seat merely failed. Both arrive with an empty
    /// or faulted tally, and a broken room must never liquidate the book. Only a complete vote
    /// with real conviction against holding closes a position.
    /// </para>
    /// </remarks>
    private static StrategyDecision? EscalatedClose(WarRoomOutcome outcome, PositionState position)
    {
        if (outcome.Verdict != WarRoomVerdict.Rejected || outcome.Operation.TradesAnything)
        {
            return null;
        }

        var tally = outcome.Tally;
        if (!tally.QuorumMet || tally.Faults > 0 || tally.Net >= 0m)
        {
            return null;
        }

        return new StrategyDecision
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = StrategyActionKind.ClosePosition,
                    ContractSymbol = position.Symbol,
                    Contracts = Math.Abs(position.Quantity),
                    Reasoning =
                        $"The room voted against holding ({tally}). Close on that vote. "
                        + Explain(outcome),
                },
            ],
            RevisedPolicy = outcome.Operation.RevisedPolicy,
            Rejection = RejectionOf(outcome),
        };
    }

    private static StrategyDecision ToDecision(WarRoomOutcome outcome)
    {
        if (outcome.ShouldExecute)
        {
            return new StrategyDecision
            {
                Actions = outcome.Operation.Actions,
                RevisedPolicy = outcome.Operation.RevisedPolicy,
            };
        }

        // A proposer that found nothing is not a rejection, so it stays a plain hold and the
        // cycle summary keeps counting zero.
        if (RejectionOf(outcome) is not { } rejection)
        {
            return StrategyDecision.Nothing(Explain(outcome));
        }

        var judged = JudgedAction(outcome);

        return StrategyDecision.Declined(
            Explain(outcome), rejection, judged?.ContractSymbol, judged?.ProfitProbability);
    }

    /// <summary>Which stage declined, or null when nothing was put forward to decline.</summary>
    private static DecisionRejection? RejectionOf(WarRoomOutcome outcome) => outcome.Verdict switch
    {
        WarRoomVerdict.PreValidationRejected => new DecisionRejection
        {
            Code = outcome.RejectionCode ?? "PRE_VALIDATION",
            Stage = "pre-validation",
        },
        WarRoomVerdict.Rejected => new DecisionRejection
        {
            Code = outcome.RejectionCode ?? "ROOM_VOTE",
            Stage = "room",
        },
        _ => null,
    };

    /// <summary>
    /// The contract the room judged, which a withdrawal no longer carries itself.
    /// </summary>
    /// <remarks>
    /// The newest review pass is searched first because a modified proposal supersedes the one
    /// before it, and the pass records what was reviewed rather than what the sitting returned.
    /// </remarks>
    private static StrategyAction? JudgedAction(WarRoomOutcome outcome) =>
        outcome.ReviewPasses
            .Reverse()
            .Select(pass => pass.Operation)
            .Append(outcome.Operation)
            .SelectMany(operation => operation.Actions)
            .FirstOrDefault(action => action.Kind != StrategyActionKind.Hold);

    private static string Explain(WarRoomOutcome outcome) => outcome.Verdict switch
    {
        WarRoomVerdict.NoProposal => "the proposer found no trade",
        WarRoomVerdict.PreValidationRejected => $"rejected before debate: {outcome.RejectionCode}",
        WarRoomVerdict.Rejected when outcome.RejectionCode is not null => outcome.RejectionCode,
        WarRoomVerdict.Rejected => $"the room did not back it ({outcome.Tally})",
        _ => "nothing to do",
    };

    private void Record(WarRoomOutcome outcome)
    {
        LastOutcome = outcome;
        _spend.Add(outcome.Cost);

        var running = _spend.Snapshot();

        _logger.LogInformation(
            "{Id}: {Verdict}. This sitting {Cost}. Running total {Total}.",
            outcome.ProposalId, outcome.Verdict, outcome.Cost, running);

        if (running.UnpricedModels.Count > 0)
        {
            _logger.LogWarning(
                "No price is known for {Models}, so the running estimate is a floor.",
                string.Join(", ", running.UnpricedModels));
        }
    }

    private string NextProposalId()
    {
        var now = _time.GetUtcNow();
        return $"proposal-{now:yyyyMMdd}-{Guid.NewGuid():N}";
    }
}

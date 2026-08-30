using Microsoft.Extensions.Logging;
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
/// replay, live trading and testing on one path.
/// </para>
/// <para>
/// This adapter owns the two things the loop should not care about: giving each proposal an
/// identity, and totalling what the room spent.
/// </para>
/// </remarks>
public sealed class WarRoomAgent(
    WarRoomSession session,
    TimeProvider time,
    ILogger logger) : IStrategyAgent, IPositionReviewer
{
    private readonly WarRoomSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TokenLedger _spend = new();

    private int _proposalCounter;

    public string Name => "war-room";

    /// <summary>Everything the room has cost since the process started.</summary>
    public RoomCost TotalCost => _spend.Snapshot();

    /// <summary>The last sitting, for the audit trail.</summary>
    public WarRoomOutcome? LastOutcome { get; private set; }

    public async Task<StrategyDecision> DecideAsync(
        StrategyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Spec §14: do not convene the room when nothing may be opened anyway.
        if (context.NewPositionsHalted || context.RemainingPositionSlots <= 0)
        {
            return StrategyDecision.Nothing(
                context.NewPositionsHalted ? "new positions are halted" : "no free position slot");
        }

        if (context.Candidates.Count == 0)
        {
            return StrategyDecision.Nothing("no candidate passed the cheap filter");
        }

        var outcome = await _session.RunAsync(
            new WarRoomRequest
            {
                ProposalId = NextProposalId(),
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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(position);

        var underReview = new PositionUnderReview
        {
            Position = position,
            TriggerReason = triggerReason,
            UnrealizedPnlFraction = unrealizedFraction,
            DaysToExpiration = daysToExpiration,
        };

        var outcome = await _session.RunAsync(
            new WarRoomRequest
            {
                ProposalId = NextProposalId(),
                Purpose = WarRoomPurpose.PositionReview,
                Market = context,
                Position = underReview,
                // ADJUST stays disabled until adjustment code is validated (spec §42).
                AllowedActions = [StrategyActionKind.ClosePosition],
            },
            cancellationToken);

        Record(outcome);

        return ToDecision(outcome);
    }

    private static StrategyDecision ToDecision(WarRoomOutcome outcome) =>
        outcome.ShouldExecute
            ? new StrategyDecision
            {
                Actions = outcome.Operation.Actions,
                RevisedPolicy = outcome.Operation.RevisedPolicy,
            }
            : StrategyDecision.Nothing(Explain(outcome));

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
        return $"proposal-{now:yyyyMMdd}-{Interlocked.Increment(ref _proposalCounter):D4}";
    }
}

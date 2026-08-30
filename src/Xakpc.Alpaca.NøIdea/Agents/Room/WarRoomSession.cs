using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>What the war room is being asked to judge.</summary>
public sealed record WarRoomRequest
{
    public required string ProposalId { get; init; }
    public required WarRoomPurpose Purpose { get; init; }
    public required StrategyContext Market { get; init; }

    /// <summary>What the proposer may put forward. A review normally allows hold and close.</summary>
    public required IReadOnlyList<StrategyActionKind> AllowedActions { get; init; }

    /// <summary>Set for a position review.</summary>
    public PositionUnderReview? Position { get; init; }
}

/// <summary>Everything one sitting produced.</summary>
public sealed record WarRoomOutcome
{
    public required string ProposalId { get; init; }
    public required WarRoomVerdict Verdict { get; init; }
    public required ProposedOperation Operation { get; init; }
    public required VoteTally Tally { get; init; }
    public required RoomCost Cost { get; init; }

    public IReadOnlyList<PersonaAnalysis> Analyses { get; init; } = [];
    public IReadOnlyList<RoomContribution> Discussion { get; init; } = [];
    public IReadOnlyList<PersonaVote> Votes { get; init; } = [];

    /// <summary>Set when C# rejected before the room sat.</summary>
    public string? RejectionCode { get; init; }

    /// <summary>True when the proposer replaced its own proposal after the debate.</summary>
    public bool ProposalWasModified { get; init; }

    public bool ShouldExecute =>
        Verdict == WarRoomVerdict.Approved && Operation.TradesAnything;
}

/// <summary>How the room runs.</summary>
public sealed record WarRoomOptions
{
    /// <summary>Discussion passes after the independent analyses.</summary>
    public int DiscussionRounds { get; init; } = 2;

    /// <summary>A hard ceiling, so a configuration mistake cannot burn the budget.</summary>
    public const int MaximumDiscussionRounds = 4;

    /// <summary>
    /// The weighted conviction a proposal must exceed. 0 means "more conviction for than
    /// against". Raising it approaches the spec's 4-of-5 recommendation.
    /// </summary>
    public decimal ApproveThreshold { get; init; }

    /// <summary>Spec §33.2. A missing or faulted vote rejects rather than shrinking quorum.</summary>
    public bool RequireEveryVoter { get; init; } = true;

    /// <summary>Wall-clock budget for one sitting. The room stops discussing when it passes.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(6);

    /// <summary>Spec §20. Let the proposer answer the room before the vote.</summary>
    public bool AllowRebuttal { get; init; } = true;
}

/// <summary>
/// One sitting of the war room: propose, analyse independently, debate, rebut, vote.
/// </summary>
/// <remarks>
/// <para>
/// <b>One class serves both callers.</b> A new trade and a position review differ only in the
/// request: the purpose, the allowed actions, and whether a position is attached. The process
/// is identical, which is what stops the two paths drifting apart.
/// </para>
/// <para>
/// The order is the value. Analyses are formed <b>independently and in parallel</b> so the
/// first speaker cannot anchor the room; only then are they shared. Votes are cast
/// <b>privately</b>, and <see cref="RoomContext"/> has no votes field at all, so privacy is a
/// property of the type rather than a rule someone has to remember.
/// </para>
/// <para>
/// <b>No persona can veto by failing.</b> A seat that throws is recorded as a fault, counted
/// as an abstention, and never as an approval (spec §33.2). Under
/// <see cref="WarRoomOptions.RequireEveryVoter"/> a fault rejects the proposal, which is the
/// conservative reading and the correct one for money.
/// </para>
/// </remarks>
public sealed class WarRoomSession(
    IProposingPersona proposer,
    IReadOnlyList<IPersona> reviewers,
    WarRoomOptions options,
    Func<ProposedOperation, WarRoomRequest, string?> preValidate,
    TimeProvider time,
    ILogger logger)
{
    private readonly IProposingPersona _proposer = proposer ?? throw new ArgumentNullException(nameof(proposer));
    private readonly IReadOnlyList<IPersona> _reviewers = reviewers ?? throw new ArgumentNullException(nameof(reviewers));
    private readonly WarRoomOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Func<ProposedOperation, WarRoomRequest, string?> _preValidate =
        preValidate ?? throw new ArgumentNullException(nameof(preValidate));

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<WarRoomOutcome> RunAsync(
        WarRoomRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ledger = new TokenLedger();
        var startedAt = _time.GetUtcNow();
        var deadline = startedAt + _options.Deadline;

        // ---- Propose (spec §15). NO_TRADE ends the sitting before any reviewer is paid.
        var operation = await _proposer.ProposeAsync(
            request.Market, request.Purpose, request.Position, request.AllowedActions,
            cancellationToken);

        CollectCost(ledger, _proposer);

        if (!operation.TradesAnything && request.Purpose == WarRoomPurpose.NewTrade)
        {
            _logger.LogInformation("{Id}: proposer returned NO_TRADE. The room does not sit.", request.ProposalId);
            return NoProposal(request, operation, ledger);
        }

        // A review that proposes HOLD is still worth discussing: holding is a decision, and
        // the room was convened because something changed.
        if (!operation.TradesAnything && request.Purpose == WarRoomPurpose.PositionReview
            && request.Position is null)
        {
            return NoProposal(request, operation, ledger);
        }

        // ---- C# pre-validation (spec §17), before the room spends tokens.
        if (_preValidate(operation, request) is { } rejection)
        {
            _logger.LogInformation("{Id}: pre-validation rejected: {Code}.", request.ProposalId, rejection);
            return Rejected(request, operation, ledger, rejection);
        }

        var context = new RoomContext
        {
            ProposalId = request.ProposalId,
            Purpose = request.Purpose,
            Market = request.Market,
            Operation = operation,
            AllowedActions = request.AllowedActions,
            Position = request.Position,
            Round = 0,
        };

        // ---- Independent analysis (spec §18). In parallel, and nobody sees anybody else.
        var analyses = await RunIndependentAsync(context, ledger, cancellationToken);

        foreach (var analysis in analyses.Where(item => item.Completed))
        {
            _logger.LogInformation(
                "  {Persona} initial {Vote} at {Confidence:P0}: {Analysis}",
                analysis.Persona, analysis.InitialVote, analysis.Confidence, analysis.Analysis);
        }

        // ---- Discussion (spec §19). Everyone has now read everyone.
        var discussion = new List<RoomContribution>();
        var rounds = Math.Clamp(_options.DiscussionRounds, 1, WarRoomOptions.MaximumDiscussionRounds);

        for (var round = 1; round <= rounds; round++)
        {
            if (_time.GetUtcNow() >= deadline)
            {
                _logger.LogWarning(
                    "{Id}: discussion deadline reached after round {Round}. Going to the vote.",
                    request.ProposalId, round - 1);
                break;
            }

            foreach (var reviewer in _reviewers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var roundContext = context with
                {
                    Analyses = analyses,
                    Said = discussion,
                    Round = round,
                };

                discussion.Add(await SpeakAsync(reviewer, roundContext, round, ledger, cancellationToken));
            }
        }

        // ---- Rebuttal (spec §20). Defend, modify, or withdraw.
        var modified = false;
        if (_options.AllowRebuttal)
        {
            var rebuttalContext = context with { Analyses = analyses, Said = discussion, Round = rounds };
            var answered = await RebutAsync(rebuttalContext, operation, ledger, cancellationToken);

            if (!answered.TradesAnything && request.Purpose == WarRoomPurpose.NewTrade)
            {
                _logger.LogInformation("{Id}: the proposer withdrew after the debate.", request.ProposalId);
                return Withdrawn(request, answered, ledger, analyses, discussion);
            }

            if (!string.Equals(answered.SubstanceKey, operation.SubstanceKey, StringComparison.Ordinal))
            {
                // Spec §20: a changed structure is a new proposal and must be validated again.
                modified = true;

                if (_preValidate(answered, request) is { } rejectedAfterChange)
                {
                    _logger.LogInformation(
                        "{Id}: the modified proposal failed pre-validation: {Code}.",
                        request.ProposalId, rejectedAfterChange);

                    return Rejected(request, answered, ledger, rejectedAfterChange) with
                    {
                        Analyses = analyses,
                        Discussion = discussion,
                        ProposalWasModified = true,
                    };
                }

                _logger.LogInformation("{Id}: the proposer modified the proposal.", request.ProposalId);
            }

            operation = answered;
            context = context with { Operation = operation };
        }

        // ---- Private vote (spec §21). In parallel, and no persona sees another vote:
        //      RoomContext carries none.
        var voteContext = context with { Analyses = analyses, Said = discussion, Round = rounds };
        var votes = await RunVoteAsync(voteContext, ledger, cancellationToken);

        foreach (var vote in votes)
        {
            _logger.LogInformation(
                "  {Persona} votes {Vote} at {Confidence:P0}: {Rationale}",
                vote.Persona, vote.Vote, vote.Confidence, vote.Rationale);
        }

        var tally = VoteTally.Count(votes, _options.ApproveThreshold, _options.RequireEveryVoter);
        var cost = ledger.Snapshot();

        _logger.LogInformation(
            "{Id}: {Tally}. Verdict {Verdict}. Cost {Cost}.",
            request.ProposalId, tally,
            tally.SizeMultiplier > 0m ? "APPROVED" : "REJECTED", cost);

        return new WarRoomOutcome
        {
            ProposalId = request.ProposalId,
            Verdict = tally.SizeMultiplier > 0m ? WarRoomVerdict.Approved : WarRoomVerdict.Rejected,
            Operation = ApplySize(operation, tally),
            Tally = tally,
            Cost = cost,
            Analyses = analyses,
            Discussion = discussion,
            Votes = votes,
            ProposalWasModified = modified,
        };
    }

    // ------------------------------------------------------------------ phases

    private async Task<IReadOnlyList<PersonaAnalysis>> RunIndependentAsync(
        RoomContext context, TokenLedger ledger, CancellationToken cancellationToken)
    {
        // Parallel on purpose: independence is the point, and it is also the cheapest place
        // to spend wall-clock time inside a 30-minute cycle.
        var work = _reviewers.Select(async reviewer =>
        {
            try
            {
                // Analyses stays empty here. A first opinion must not see another.
                var analysis = await reviewer.AnalyseAsync(
                    context with { Analyses = [], Said = [], Round = 0 }, cancellationToken);

                CollectCost(ledger, reviewer);
                return analysis;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError(error, "{Persona} failed its independent analysis.", reviewer.Name);
                return PersonaAnalysis.Failed(reviewer.Name, error.Message);
            }
        });

        return await Task.WhenAll(work);
    }

    private async Task<RoomContribution> SpeakAsync(
        IPersona reviewer, RoomContext context, int round, TokenLedger ledger,
        CancellationToken cancellationToken)
    {
        try
        {
            var contribution = await reviewer.ParticipateAsync(context, cancellationToken);
            CollectCost(ledger, reviewer);

            if (contribution.Spoke)
            {
                _logger.LogInformation("  [{Round}] {Speaker}: {Summary}", round, contribution.Speaker, contribution.Summary);
            }

            return contribution;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Skipped, never fatal. A seat that throws must not end the sitting.
            _logger.LogError(error, "{Persona} failed to speak.", reviewer.Name);
            return RoomContribution.Failed(reviewer.Name, round, error.Message);
        }
    }

    private async Task<ProposedOperation> RebutAsync(
        RoomContext context, ProposedOperation fallback, TokenLedger ledger,
        CancellationToken cancellationToken)
    {
        try
        {
            var answered = await _proposer.RebutAsync(context, cancellationToken);
            CollectCost(ledger, _proposer);
            return answered;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed rebuttal leaves the original standing. The room still votes on it, so
            // a broken proposer cannot quietly withdraw a proposal it already justified.
            _logger.LogError(error, "The proposer failed to answer the room. The proposal stands.");
            return fallback;
        }
    }

    private async Task<IReadOnlyList<PersonaVote>> RunVoteAsync(
        RoomContext context, TokenLedger ledger, CancellationToken cancellationToken)
    {
        var work = _reviewers.Select(async reviewer =>
        {
            try
            {
                var vote = await reviewer.VoteAsync(context, cancellationToken);
                CollectCost(ledger, reviewer);
                return vote;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError(error, "{Persona} failed to vote.", reviewer.Name);
                return PersonaVote.Abstained(reviewer.Name, error.Message);
            }
        });

        return await Task.WhenAll(work);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Scales the operation by the room's conviction.</summary>
    private static ProposedOperation ApplySize(ProposedOperation operation, VoteTally tally) =>
        operation with
        {
            Actions = operation.Actions
                .Select(action => action.Kind == StrategyActionKind.Hold
                    ? action
                    : action with { Contracts = tally.ContractsFor(action.Contracts) })
                .Where(action => action.Kind == StrategyActionKind.Hold || action.Contracts > 0)
                .ToArray(),
        };

    /// <summary>
    /// Folds a persona's own ledger in, when it keeps one.
    /// </summary>
    /// <remarks>
    /// A persona with no model — the deterministic seat — reports nothing and costs nothing,
    /// which is exactly what should appear in the report.
    /// </remarks>
    private static void CollectCost(TokenLedger ledger, IPersona persona)
    {
        if (persona is ICostReporting reporting)
        {
            ledger.Add(reporting.DrainCost());
        }
    }

    private static WarRoomOutcome NoProposal(
        WarRoomRequest request, ProposedOperation operation, TokenLedger ledger) => new()
    {
        ProposalId = request.ProposalId,
        Verdict = WarRoomVerdict.NoProposal,
        Operation = operation,
        Tally = VoteTally.Count([]),
        Cost = ledger.Snapshot(),
    };

    private static WarRoomOutcome Rejected(
        WarRoomRequest request, ProposedOperation operation, TokenLedger ledger, string code) => new()
    {
        ProposalId = request.ProposalId,
        Verdict = WarRoomVerdict.PreValidationRejected,
        Operation = operation,
        Tally = VoteTally.Count([]),
        Cost = ledger.Snapshot(),
        RejectionCode = code,
    };

    private static WarRoomOutcome Withdrawn(
        WarRoomRequest request,
        ProposedOperation operation,
        TokenLedger ledger,
        IReadOnlyList<PersonaAnalysis> analyses,
        IReadOnlyList<RoomContribution> discussion) => new()
    {
        ProposalId = request.ProposalId,
        Verdict = WarRoomVerdict.Rejected,
        Operation = operation,
        Tally = VoteTally.Count([]),
        Cost = ledger.Snapshot(),
        Analyses = analyses,
        Discussion = discussion,
        RejectionCode = "WITHDRAWN_BY_PROPOSER",
        ProposalWasModified = true,
    };
}

/// <summary>A persona that spends money and can say how much.</summary>
/// <remarks>
/// Separate from <see cref="IPersona"/> so a deterministic seat is not obliged to pretend it
/// has a bill.
/// </remarks>
public interface ICostReporting
{
    /// <summary>Returns what has been spent since the last call, and resets.</summary>
    RoomCost DrainCost();
}

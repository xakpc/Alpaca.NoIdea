using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The war-room flow. Every persona is scripted; no model is called.
/// </summary>
/// <remarks>
/// Two properties matter more than the rest, and both are structural rather than advisory:
/// an independent analysis must not see another, and a vote must not see another vote. The
/// rest of these tests pin the failure behaviour, because a broken seat must never be able to
/// decide anything.
/// </remarks>
public class WarRoomTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 14, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- independence

    [Fact]
    public async Task AnIndependentAnalysisCannotSeeAnotherAnalysis()
    {
        // The anti-anchoring property. Without it the first speaker sets the room's opinion
        // and the extra seats are just cost.
        var watcher = new RecordingPersona("watcher", VoteKind.Approve, 0.6m);
        var other = new RecordingPersona("other", VoteKind.Approve, 0.6m);

        await Session(Proposer(OneTrade()), [watcher, other]).RunAsync(Request(), CancellationToken.None);

        Assert.Empty(watcher.AnalysisContext!.Analyses);
        Assert.Empty(watcher.AnalysisContext.Said);
    }

    [Fact]
    public async Task DiscussionSeesEveryIndependentAnalysis()
    {
        var watcher = new RecordingPersona("watcher", VoteKind.Approve, 0.6m);
        var other = new RecordingPersona("other", VoteKind.Reject, 0.4m);

        await Session(Proposer(OneTrade()), [other, watcher]).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(2, watcher.DiscussionContext!.Analyses.Count);
    }

    // ---------------------------------------------------------------- vote privacy

    [Fact]
    public void TheContextGivenToAPersonaCarriesNoVotes()
    {
        // Privacy is a property of the type, not a rule a caller has to remember. If someone
        // later adds a votes field to RoomContext, this test is what stops it shipping.
        Assert.DoesNotContain(
            typeof(RoomContext).GetProperties(),
            property => property.PropertyType == typeof(IReadOnlyList<PersonaVote>));
    }

    [Fact]
    public async Task AVoterCannotSeeAnotherVote()
    {
        var watcher = new RecordingPersona("watcher", VoteKind.Approve, 0.6m);

        await Session(Proposer(OneTrade()), [watcher, new RecordingPersona("other", VoteKind.Reject, 0.9m)])
            .RunAsync(Request(), CancellationToken.None);

        // All it ever saw was the discussion. There is nowhere for a vote to have reached it.
        Assert.NotNull(watcher.VoteContext);
        Assert.DoesNotContain(
            watcher.VoteContext!.GetType().GetProperties(),
            property => property.Name.Contains("Vote", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- cost control

    [Fact]
    public async Task NoTradeSpendsNothingOnReviewers()
    {
        var reviewer = new RecordingPersona("reviewer", VoteKind.Approve, 0.6m);

        var outcome = await Session(Proposer(ProposedOperation.Nothing("nothing worth trading")), [reviewer])
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.NoProposal, outcome.Verdict);
        Assert.Equal(0, reviewer.AnalyseCalls);
    }

    [Fact]
    public async Task PreValidationRejectsBeforeAnyReviewerIsPaid()
    {
        var reviewer = new RecordingPersona("reviewer", VoteKind.Approve, 0.6m);

        var outcome = await Session(
                Proposer(OneTrade()), [reviewer], preValidate: (_, _) => "REJECT_BAD_QUOTE")
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.PreValidationRejected, outcome.Verdict);
        Assert.Equal("REJECT_BAD_QUOTE", outcome.RejectionCode);
        Assert.Equal(0, reviewer.AnalyseCalls);
    }

    [Fact]
    public async Task EveryReviewerSpeaksOncePerDiscussionRound()
    {
        var reviewer = new RecordingPersona("reviewer", VoteKind.Approve, 0.6m);

        await Session(Proposer(OneTrade()), [reviewer], new WarRoomOptions { DiscussionRounds = 2 })
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(1, reviewer.AnalyseCalls);
        Assert.Equal(2, reviewer.SpeakCalls);
        Assert.Equal(1, reviewer.VoteCalls);
    }

    [Fact]
    public async Task RoundsAreCappedSoAConfigurationMistakeCannotBurnTheBudget()
    {
        var reviewer = new RecordingPersona("reviewer", VoteKind.Approve, 0.6m);

        await Session(Proposer(OneTrade()), [reviewer], new WarRoomOptions { DiscussionRounds = 99 })
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomOptions.MaximumDiscussionRounds, reviewer.SpeakCalls);
    }

    // ---------------------------------------------------------------- failure never decides

    [Fact]
    public async Task AReviewerThatThrowsIsAnAbstentionAndNeverAnApproval()
    {
        var outcome = await Session(
                Proposer(OneTrade()),
                [new ThrowingPersona("broken"), new RecordingPersona("good", VoteKind.Approve, 0.9m)])
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(1, outcome.Tally.Faults);
        Assert.Equal(1, outcome.Tally.Approvals);

        // RequireEveryVoter is on: a faulted seat rejects rather than shrinking the quorum.
        Assert.False(outcome.Tally.QuorumMet);
        Assert.Equal(WarRoomVerdict.Rejected, outcome.Verdict);
    }

    [Fact]
    public async Task AFailedProposerBecomesNoTradeRatherThanAnException()
    {
        var outcome = await Session(new ThrowingProposer(), [new RecordingPersona("r", VoteKind.Approve, 0.6m)])
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.NoProposal, outcome.Verdict);
    }

    [Fact]
    public async Task AFailedRebuttalLeavesTheOriginalProposalStanding()
    {
        // A broken proposer must not be able to withdraw a proposal it already justified.
        var proposer = new ScriptedProposer(OneTrade()) { ThrowOnRebut = true };

        var outcome = await Session(proposer, [new RecordingPersona("r", VoteKind.Approve, 0.8m)])
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.Approved, outcome.Verdict);
        Assert.True(outcome.Operation.TradesAnything);
    }

    // ---------------------------------------------------------------- rebuttal

    [Fact]
    public async Task AWithdrawnProposalIsRejectedWithoutAVote()
    {
        var reviewer = new RecordingPersona("r", VoteKind.Approve, 0.9m);
        var proposer = new ScriptedProposer(OneTrade()) { Rebuttal = ProposedOperation.Nothing("withdrawn") };

        var outcome = await Session(proposer, [reviewer]).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.Rejected, outcome.Verdict);
        Assert.Equal("WITHDRAWN_BY_PROPOSER", outcome.RejectionCode);
        Assert.Equal(0, reviewer.VoteCalls);
    }

    [Fact]
    public async Task AModifiedProposalIsPreValidatedAgain()
    {
        // Spec §20. Without this the room can talk itself into a structurally invalid trade.
        var validations = 0;
        var proposer = new ScriptedProposer(OneTrade())
        {
            Rebuttal = TradeOn("TEST260904C00105000"),
        };

        var outcome = await Session(
                proposer,
                [new RecordingPersona("r", VoteKind.Approve, 0.8m)],
                preValidate: (_, _) =>
                {
                    validations++;
                    return validations >= 2 ? "REJECT_EXPIRATION" : null;
                })
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(2, validations);
        Assert.Equal(WarRoomVerdict.PreValidationRejected, outcome.Verdict);
        Assert.True(outcome.ProposalWasModified);
    }

    [Fact]
    public async Task AModifiedProposalGetsAFreshReviewAndKeepsBothVersions()
    {
        var reviewer = new RecordingPersona("r", VoteKind.Approve, 0.8m);
        var original = OneTrade();
        var changed = TradeOn("TEST260904C00105000");
        var proposer = new ScriptedProposer(original) { Rebuttal = changed };

        var outcome = await Session(proposer, [reviewer])
            .RunAsync(Request(), CancellationToken.None);

        Assert.Equal(2, reviewer.AnalyseCalls);
        Assert.Equal(2, reviewer.SpeakCalls);
        Assert.Equal(1, reviewer.VoteCalls);
        Assert.Collection(
            outcome.ReviewPasses,
            first =>
            {
                Assert.Equal(1, first.ProposalVersion);
                Assert.True(first.Superseded);
                Assert.Equal(original.SubstanceKey, first.Operation.SubstanceKey);
                Assert.Empty(first.Votes);
            },
            second =>
            {
                Assert.Equal(2, second.ProposalVersion);
                Assert.False(second.Superseded);
                Assert.Equal(changed.SubstanceKey, second.Operation.SubstanceKey);
                Assert.Single(second.Votes);
            });
    }

    // ---------------------------------------------------------------- sizing

    [Fact]
    public async Task ConvictionScalesTheContractCount()
    {
        // Four contracts proposed; two seats approve at 0.5 confidence, so net is 0.5.
        var proposer = Proposer(TradeOn("TEST260904C00100000", contracts: 4));

        var outcome = await Session(proposer,
        [
            new RecordingPersona("a", VoteKind.Approve, 0.5m),
            new RecordingPersona("b", VoteKind.Approve, 0.5m),
        ]).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.Approved, outcome.Verdict);
        Assert.Equal(0.5m, outcome.Tally.Net);
        Assert.Equal(2, outcome.Operation.Actions.Single().Contracts);
    }

    [Fact]
    public async Task AnOpposedProposalIsRejectedAndTradesNothing()
    {
        var outcome = await Session(Proposer(OneTrade()),
        [
            new RecordingPersona("a", VoteKind.Reject, 0.9m),
            new RecordingPersona("b", VoteKind.Approve, 0.2m),
        ]).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(WarRoomVerdict.Rejected, outcome.Verdict);
        Assert.False(outcome.ShouldExecute);
    }

    // ---------------------------------------------------------------- reuse

    [Fact]
    public async Task TheSameSessionServesAPositionReview()
    {
        // The whole point of the refactor: one process, two callers.
        var reviewer = new RecordingPersona("r", VoteKind.Approve, 0.8m);
        var proposer = Proposer(new ProposedOperation
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = StrategyActionKind.ClosePosition,
                    ContractSymbol = "TEST260904C00100000",
                    Contracts = 1,
                    Reasoning = "thesis broken",
                },
            ],
            Thesis = "the catalyst passed without the move",
        });

        var request = Request() with
        {
            Purpose = WarRoomPurpose.PositionReview,
            AllowedActions = [StrategyActionKind.ClosePosition],
            Position = new PositionUnderReview
            {
                Position = new PositionState
                {
                    Symbol = "TEST260904C00100000",
                    Quantity = 1,
                    AverageEntryPrice = 1.00m,
                },
                TriggerReason = "loss milestone",
            },
        };

        var outcome = await Session(proposer, [reviewer]).RunAsync(request, CancellationToken.None);

        Assert.Equal(WarRoomVerdict.Approved, outcome.Verdict);
        Assert.Equal(StrategyActionKind.ClosePosition, outcome.Operation.Actions.Single().Kind);
        Assert.Equal(WarRoomPurpose.PositionReview, reviewer.AnalysisContext!.Purpose);
    }

    // ---------------------------------------------------------------- helpers

    private static WarRoomSession Session(
        IProposingPersona proposer,
        IReadOnlyList<IPersona> reviewers,
        WarRoomOptions? options = null,
        Func<ProposedOperation, WarRoomRequest, string?>? preValidate = null) =>
        new(proposer, reviewers, options ?? new WarRoomOptions { DiscussionRounds = 1 },
            preValidate ?? ((_, _) => null), new FakeClock(Now), NullLogger.Instance);

    private static ScriptedProposer Proposer(ProposedOperation operation) => new(operation);

    private static ProposedOperation OneTrade() => TradeOn("TEST260904C00100000");

    private static ProposedOperation TradeOn(string symbol, int contracts = 1) => new()
    {
        Actions =
        [
            new StrategyAction
            {
                Kind = StrategyActionKind.OpenCall,
                ContractSymbol = symbol,
                Contracts = contracts,
                Probability = 0.6m,
                Reasoning = "test",
            },
        ],
        Thesis = "test thesis",
    };

    private static WarRoomRequest Request() => new()
    {
        ProposalId = "proposal-test-0001",
        Purpose = WarRoomPurpose.NewTrade,
        AllowedActions = [StrategyActionKind.OpenCall, StrategyActionKind.OpenPut],
        Market = new StrategyContext
        {
            NowUtc = Now,
            Account = new AccountState
            {
                AccountNumber = "TEST",
                Equity = 100_000m,
                Cash = 100_000m,
                BuyingPower = 100_000m,
                IsTradingBlocked = false,
                IsAccountBlocked = false,
            },
            Positions = [],
            ContractCatalog = [],
            Policy = new StrategyPolicy(),
            RemainingPositionSlots = 4,
            NewPositionsHalted = false,
        },
    };

    private sealed class ScriptedProposer(ProposedOperation operation) : IProposingPersona
    {
        public string Name => "proposer";
        public ModelProvider Provider => ModelProvider.None;
        public ProposedOperation? Rebuttal { get; set; }
        public bool ThrowOnRebut { get; set; }

        public Task<ProposedOperation> ProposeAsync(
            StrategyContext market, WarRoomPurpose purpose, PositionUnderReview? position,
            IReadOnlyList<StrategyActionKind> allowedActions, CancellationToken cancellationToken) =>
            Task.FromResult(operation);

        public Task<ProposedOperation> RebutAsync(RoomContext context, CancellationToken cancellationToken) =>
            ThrowOnRebut
                ? throw new InvalidOperationException("rebuttal broke")
                : Task.FromResult(Rebuttal ?? operation);

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException("the proposer does not review");

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException("the proposer does not review");

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException("the proposer does not vote");
    }

    private sealed class ThrowingProposer : IProposingPersona
    {
        public string Name => "broken-proposer";
        public ModelProvider Provider => ModelProvider.None;

        public Task<ProposedOperation> ProposeAsync(
            StrategyContext m, WarRoomPurpose p, PositionUnderReview? pos,
            IReadOnlyList<StrategyActionKind> a, CancellationToken t) =>
            Task.FromResult(ProposedOperation.Nothing("the proposer failed"));

        public Task<ProposedOperation> RebutAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(ProposedOperation.Nothing("failed"));

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException();

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException();

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            throw new NotSupportedException();
    }

    /// <summary>Records what it was shown, so the tests can assert on isolation.</summary>
    private sealed class RecordingPersona(string name, VoteKind vote, decimal confidence) : IPersona
    {
        public string Name => name;
        public ModelProvider Provider => ModelProvider.None;

        public int AnalyseCalls { get; private set; }
        public int SpeakCalls { get; private set; }
        public int VoteCalls { get; private set; }

        public RoomContext? AnalysisContext { get; private set; }
        public RoomContext? DiscussionContext { get; private set; }
        public RoomContext? VoteContext { get; private set; }

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext context, CancellationToken cancellationToken)
        {
            AnalyseCalls++;
            AnalysisContext = context;

            return Task.FromResult(new PersonaAnalysis
            {
                Persona = name,
                InitialVote = vote,
                Confidence = confidence,
                Analysis = $"{name} analysis",
            });
        }

        public Task<RoomContribution> ParticipateAsync(RoomContext context, CancellationToken cancellationToken)
        {
            SpeakCalls++;
            DiscussionContext = context;

            return Task.FromResult(new RoomContribution
            {
                Speaker = name,
                Round = context.Round,
                Summary = $"{name} speaks",
            });
        }

        public Task<PersonaVote> VoteAsync(RoomContext context, CancellationToken cancellationToken)
        {
            VoteCalls++;
            VoteContext = context;

            return Task.FromResult(new PersonaVote
            {
                Persona = name,
                Vote = vote,
                Confidence = confidence,
                Rationale = $"{name} votes",
            });
        }
    }

    private sealed class ThrowingPersona(string name) : IPersona
    {
        public string Name => name;
        public ModelProvider Provider => ModelProvider.None;

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            throw new InvalidOperationException("this seat is broken");

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            throw new InvalidOperationException("this seat is broken");

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            throw new InvalidOperationException("this seat is broken");
    }
}

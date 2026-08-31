using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The audit trail: what the agent thought, and what the guardrails allowed.
/// </summary>
/// <remarks>
/// These tables were created at every startup and written by nothing, so the war room's
/// reasoning existed only in memory and in the log. The queries here are the demo questions
/// in <c>.lode/operations/observability.md</c> asked against real rows.
/// </remarks>
public class AuditTrailTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"audit-{Guid.NewGuid():N}.db");

    private TradingStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new TradingStore(TradingStore.ConnectionStringForFile(_databasePath));
        await _store.CreateSchemaAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ASittingIsStoredWithEverySeatAndReadBack()
    {
        var runId = await _store.RecordEvaluationAsync(Evaluation("accepted"), CancellationToken.None);

        await _store.RecordForecastsAsync(
            [
                Forecast(runId, "skeptic", "Reject", 0.30m),
                Forecast(runId, "quant", "Approve", 0.58m),

                // The plain-C# seat votes and computes, but never produces a probability.
                // A NOT NULL column here would silently drop it from the record.
                Forecast(runId, "exposure", "Approve", probability: null),
            ],
            CancellationToken.None);

        var decisionId = await _store.RecordDecisionAsync(
            new DecisionRow
            {
                RunId = runId,
                CombinedProbability = 0.58m,
                MarketProbability = 0.44m,
                Edge = 0.14m,
                NetVote = 0.21m,
                Action = "OpenCall",
                Reason = "the catalyst is not yet priced",
                RiskResult = "allowed",
                CreatedUtc = 1_756_000_000,
            },
            CancellationToken.None);

        await _store.ReserveAsync(
            new OrderRecord
            {
                ClientOrderId = "live-abc",
                OptionSymbol = "SPY260904C00770000",
                Side = "Buy",
                Quantity = 2,
                OrderType = "Limit",
                LimitPrice = 3.15m,
                SubmittedUtc = 1_756_000_000,
                Status = "reserved",
                Mode = "live",
                DecisionId = decisionId,
            },
            CancellationToken.None);

        var entries = await _store.RecentDecisionsAsync(10, CancellationToken.None);
        var entry = Assert.Single(entries);

        Assert.Equal("accepted", entry.Status);
        Assert.Equal("OpenCall", entry.Action);
        Assert.Equal(0.58m, entry.CombinedProbability);
        Assert.Equal(0.44m, entry.MarketProbability);
        Assert.Equal(0.21m, entry.NetVote);
        Assert.Equal(3, entry.SeatCount);
        Assert.Equal("live-abc", entry.ClientOrderId);
    }

    [Fact]
    public async Task ARejectionIsStoredWithTheRuleThatRefusedIt()
    {
        // A run that stores only its trades cannot show that a guardrail ever fired.
        var runId = await _store.RecordEvaluationAsync(Evaluation("rejected"), CancellationToken.None);

        await _store.RecordDecisionAsync(
            new DecisionRow
            {
                RunId = runId,
                Action = "OpenCall",
                Reason = "the catalyst is not yet priced",
                RiskResult = "risk guard: the spread is 14% of the ask, over the 10% limit",
                CreatedUtc = 1_756_000_000,
            },
            CancellationToken.None);

        var entry = Assert.Single(await _store.RecentDecisionsAsync(10, CancellationToken.None));

        Assert.Equal("rejected", entry.Status);
        Assert.Contains("over the 10% limit", entry.RiskResult);

        // No order, and that is the point of the LEFT JOIN.
        Assert.Null(entry.ClientOrderId);
        Assert.Null(entry.CombinedProbability);
    }

    [Fact]
    public async Task RowCountsStartAtZeroAndFollowTheWrites()
    {
        var before = await _store.AuditRowCountsAsync(CancellationToken.None);
        Assert.Equal(0, before["evaluation_runs"]);
        Assert.Equal(0, before["decisions"]);

        var runId = await _store.RecordEvaluationAsync(Evaluation("accepted"), CancellationToken.None);
        await _store.RecordForecastsAsync([Forecast(runId, "quant", "Approve", 0.6m)], CancellationToken.None);

        var after = await _store.AuditRowCountsAsync(CancellationToken.None);
        Assert.Equal(1, after["evaluation_runs"]);
        Assert.Equal(0, after["decisions"]);
    }

    [Fact]
    public async Task ADryRunOrderIsNeverReadBackAsALiveOne()
    {
        await _store.ReserveAsync(
            new OrderRecord
            {
                ClientOrderId = "dry-run-xyz",
                OptionSymbol = "SPY260904C00770000",
                Side = "Buy",
                Quantity = 1,
                OrderType = "Limit",
                SubmittedUtc = 1_756_000_000,
                Status = "reserved",
                Mode = "dry-run",
            },
            CancellationToken.None);

        var runId = await _store.RecordEvaluationAsync(
            Evaluation("accepted") with { Mode = "dry-run" }, CancellationToken.None);

        await _store.RecordDecisionAsync(
            new DecisionRow { RunId = runId, Action = "OpenCall", CreatedUtc = 1 },
            CancellationToken.None);

        var entry = Assert.Single(await _store.RecentDecisionsAsync(10, CancellationToken.None));
        Assert.Equal("dry-run", entry.Mode);
    }

    private static EvaluationRunRow Evaluation(string status) => new()
    {
        TimestampUtc = 1_756_000_000,
        Mode = "live",
        ProposalId = "proposal-20260831-0007",
        Symbol = "SPY",
        CurrentPrice = 770.25m,
        OptionSymbol = "SPY260904C00770000",
        OptionType = "call",
        Strike = 770m,
        ExpirationUtc = 1_756_900_000,
        MarketProbability = 0.44m,
        Status = status,
        MarketSnapshotJson = """{"Bid":3.10,"Ask":3.20}""",
    };

    private static ForecastRow Forecast(
        long runId, string seat, string vote, decimal? probability) => new()
    {
        RunId = runId,
        Forecaster = seat,
        Vote = vote,
        Probability = probability,
        Confidence = 0.7m,
        Reasoning = "a rationale",
        EvidenceJson = """{"initialVote":"Reject"}""",
        CreatedUtc = 1_756_000_000,
    };
}

/// <summary>
/// The projection that turns a sitting into audit rows.
/// </summary>
/// <remarks>
/// This is the path that fills <c>forecasts</c>, and no stub-agent run reaches it: the stub
/// has no room. Without these tests the seat detail would ship unexercised.
/// </remarks>
public class SeatOpinionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EverySeatBecomesOneOpinionCarryingBothItsAnalysisAndItsVote()
    {
        var agent = await SatRoomAsync();
        var opinions = agent.LastOpinions;

        Assert.Equal(["quant", "skeptic"], opinions.Select(o => o.Seat).Order().ToArray());

        var skeptic = opinions.Single(o => o.Seat == "skeptic");
        Assert.Equal("Reject", skeptic.Vote);
        Assert.Equal(0.7m, skeptic.Confidence);
        Assert.Equal("skeptic votes", skeptic.Reasoning);

        // The change of mind is the interesting part, so the first opinion is kept beside
        // the final vote rather than being overwritten by it.
        Assert.Contains("\"initialVote\":\"Reject\"", skeptic.EvidenceJson);
        Assert.Contains("skeptic analysis", skeptic.EvidenceJson);
        Assert.Contains("skeptic speaks", skeptic.EvidenceJson);
    }

    [Fact]
    public async Task TheSittingIsIdentifiedAndTheVoteIsCarried()
    {
        var agent = await SatRoomAsync();

        Assert.NotNull(agent.LastProposalId);
        Assert.NotNull(agent.LastNetVote);
    }

    [Fact]
    public void AnAgentThatNeverSatExplainsNothing()
    {
        var agent = new WarRoomAgent(
            new WarRoomSession(
                new SilentProposer(), [], new WarRoomOptions { DiscussionRounds = 1 },
                (_, _) => null, new FakeClock(Now), NullLogger.Instance),
            new FakeClock(Now), NullLogger.Instance);

        Assert.Empty(agent.LastOpinions);
        Assert.Null(agent.LastProposalId);
        Assert.Null(agent.LastNetVote);
    }

    private static async Task<WarRoomAgent> SatRoomAsync()
    {
        var session = new WarRoomSession(
            new StubProposer(),
            [new StubSeat("skeptic", VoteKind.Reject, 0.7m), new StubSeat("quant", VoteKind.Approve, 0.6m)],
            new WarRoomOptions { DiscussionRounds = 1 },
            (_, _) => null, new FakeClock(Now), NullLogger.Instance);

        var agent = new WarRoomAgent(session, new FakeClock(Now), NullLogger.Instance);
        await agent.DecideAsync(Context(), CancellationToken.None);
        return agent;
    }

    private static StrategyContext Context() => new()
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
        Candidates =
        [
            new CandidateView
            {
                Candidate = new OptionCandidate
                {
                    ContractSymbol = "TEST260904C00100000",
                    Underlying = "TEST",
                    OptionType = "call",
                    Strike = 100m,
                    Expiration = new DateOnly(2026, 9, 4),
                    Quality = QuoteQuality.TwoSided,
                    ReferencePrice = 1.20m,
                    Bid = 1.15m,
                    Ask = 1.20m,
                },
                UnderlyingPrice = 99m,
            },
        ],
        Policy = new StrategyPolicy(),
        RemainingPositionSlots = 4,
        NewPositionsHalted = false,
    };

    private sealed class StubProposer : IProposingPersona
    {
        public string Name => "proposer";
        public ModelProvider Provider => ModelProvider.None;

        public Task<ProposedOperation> ProposeAsync(
            StrategyContext market, WarRoomPurpose purpose, PositionUnderReview? position,
            IReadOnlyList<StrategyActionKind> allowedActions, CancellationToken t) =>
            Task.FromResult(new ProposedOperation
            {
                Actions =
                [
                    new StrategyAction
                    {
                        Kind = StrategyActionKind.OpenCall,
                        ContractSymbol = "TEST260904C00100000",
                        Contracts = 1,
                        Probability = 0.6m,
                        Reasoning = "test",
                    },
                ],
                Thesis = "test thesis",
            });

        public Task<ProposedOperation> RebutAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(c.Operation);

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaAnalysis
            {
                Persona = Name, InitialVote = VoteKind.Approve, Analysis = "proposer analysis",
            });

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new RoomContribution { Speaker = Name, Round = c.Round, Summary = "proposer speaks" });

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaVote { Persona = Name, Vote = VoteKind.Approve, Rationale = "proposer votes" });
    }

    private sealed class SilentProposer : IProposingPersona
    {
        public string Name => "proposer";
        public ModelProvider Provider => ModelProvider.None;

        public Task<ProposedOperation> ProposeAsync(
            StrategyContext market, WarRoomPurpose purpose, PositionUnderReview? position,
            IReadOnlyList<StrategyActionKind> allowedActions, CancellationToken t) =>
            Task.FromResult(ProposedOperation.Nothing("nothing worth doing"));

        public Task<ProposedOperation> RebutAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(c.Operation);

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaAnalysis
            {
                Persona = Name, InitialVote = VoteKind.Abstain, Analysis = "",
            });

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new RoomContribution { Speaker = Name, Round = 0, Summary = "" });

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaVote { Persona = Name, Vote = VoteKind.Abstain, Rationale = "" });
    }

    private sealed class StubSeat(string name, VoteKind vote, decimal confidence) : IPersona
    {
        public string Name => name;
        public ModelProvider Provider => ModelProvider.None;

        public Task<PersonaAnalysis> AnalyseAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaAnalysis
            {
                Persona = name,
                InitialVote = vote,
                Confidence = confidence,
                Analysis = $"{name} analysis",
            });

        public Task<RoomContribution> ParticipateAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new RoomContribution
            {
                Speaker = name,
                Round = c.Round,
                Summary = $"{name} speaks",
            });

        public Task<PersonaVote> VoteAsync(RoomContext c, CancellationToken t) =>
            Task.FromResult(new PersonaVote
            {
                Persona = name,
                Vote = vote,
                Confidence = confidence,
                Rationale = $"{name} votes",
            });
    }
}

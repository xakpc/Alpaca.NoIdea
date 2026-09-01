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
    public async Task ACompleteSittingToolCallDecisionAndOrderRemainLinked()
    {
        const string proposalId = "proposal-20260831-audit";
        await _store.BeginSittingAsync(
            proposalId, "live", WarRoomPurpose.NewTrade, 100, CancellationToken.None);
        await _store.RecordToolCallsAsync(
            proposalId,
            [new AgentToolCallAudit("quant", "analysis", "model", "call-1", "search", "{}", "{}", "completed")],
            CancellationToken.None);

        var operation = new ProposedOperation
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = StrategyActionKind.OpenCall,
                    ContractSymbol = "SPY260904C00770000",
                    Contracts = 1,
                    Reasoning = "test",
                },
            ],
            Thesis = "final thesis",
            ThesisConditions = ["SPY stays above 765"],
        };
        await _store.CompleteSittingAsync(
            proposalId,
            WarRoomVerdict.Approved,
            [new ProposalReviewPass
            {
                ProposalId = proposalId,
                ProposalVersion = 1,
                ReviewPass = 1,
                Operation = operation,
                Verdict = WarRoomVerdict.Approved,
                Tally = VoteTally.Count([], requireEveryVoter: false),
            }],
            101,
            CancellationToken.None);

        var eventId = await _store.RecordDecisionAndReserveAsync(
            Decision("accepted") with { ProposalId = proposalId },
            new OrderRecord
            {
                CorrelationId = "live-abc",
                ClientOrderId = "live-abc",
                OptionSymbol = "SPY260904C00770000",
                Side = "Buy",
                Quantity = 1,
                OrderType = "Limit",
                LimitPrice = 3.15m,
                SubmittedUtc = 102,
                Status = OrderLifecycle.Reserved.ToString(),
                Mode = "live",
            },
            CancellationToken.None);

        var entry = Assert.Single(await _store.RecentDecisionsAsync(10, CancellationToken.None));
        Assert.True(eventId > 0);
        Assert.Equal("accepted", entry.Outcome);
        Assert.Equal(1, entry.ToolCallCount);
        Assert.Equal("live-abc", entry.CorrelationId);
        Assert.Empty(await _store.AuditIntegrityAsync(CancellationToken.None));

        var thesis = Assert.Single(await _store.PositionThesesAsync(
            "live", ["SPY260904C00770000"], CancellationToken.None)).Value;
        Assert.Equal("final thesis", thesis.Thesis);
    }

    [Fact]
    public async Task ARejectedDecisionHasNoOrderAndKeepsTheRule()
    {
        await _store.RecordDecisionEventAsync(
            Decision("rejected") with
            {
                RiskResult = "risk guard: spread over limit",
            },
            CancellationToken.None);

        var entry = Assert.Single(await _store.RecentDecisionsAsync(10, CancellationToken.None));
        Assert.Equal("rejected", entry.Outcome);
        Assert.Contains("spread over limit", entry.RiskResult);
        Assert.Null(entry.CorrelationId);
    }

    [Fact]
    public async Task AuditCountsUseOnlyTheLiveOnlySchema()
    {
        var counts = await _store.AuditRowCountsAsync(CancellationToken.None);
        Assert.Equal(TradingStore.CurrentSchemaVersion,
            await _store.SchemaVersionAsync(CancellationToken.None));
        Assert.Equal(6, counts.Count);
        Assert.All(counts.Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ActivePolicyAndReviewCursorSurviveAStoreRestart()
    {
        var policy = new StrategyPolicy
        {
            TakeProfitFraction = 0.35m,
            StopLossFraction = 0.25m,
            Rationale = "durable test policy",
        };
        var reviewed = new DateTimeOffset(2026, 8, 31, 15, 30, 0, TimeSpan.Zero);

        await _store.SavePolicyAsync("live", policy, reviewed, CancellationToken.None);
        await _store.SavePositionReviewStateAsync(
            "live", "SPY260904C00770000", reviewed, 7, CancellationToken.None);

        var restarted = new TradingStore(TradingStore.ConnectionStringForFile(_databasePath));
        Assert.Equal(policy, await restarted.LoadPolicyAsync("live", CancellationToken.None));
        var cursor = Assert.Single(await restarted.LoadPositionReviewStateAsync(
            "live", CancellationToken.None));
        Assert.Equal(reviewed, cursor.LastReviewedUtc);
        Assert.Equal(7, cursor.LastNewsSeen);
    }

    [Fact]
    public async Task SchemaTwoRequiresAnExplicitArchiveBeforeSchemaThreeStarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"schema-two-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             TradingStore.ConnectionStringForFile(path)))
            {
                await connection.OpenAsync();
                await new Microsoft.Data.Sqlite.SqliteCommand(
                    "PRAGMA user_version = 2;", connection).ExecuteNonQueryAsync();
            }

            var old = new TradingStore(TradingStore.ConnectionStringForFile(path));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => old.CreateSchemaAsync(CancellationToken.None));
            Assert.Contains("archive trader.db", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static DecisionEventRow Decision(string outcome) => new()
    {
        TimestampUtc = 1_756_000_000,
        Mode = "live",
        Purpose = "new-trade",
        Action = "OpenCall",
        Outcome = outcome,
        Reason = "the catalyst is not yet priced",
        Symbol = "SPY",
        OptionSymbol = "SPY260904C00770000",
        OptionType = "call",
        Strike = 770m,
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
        Assert.Contains("initialVote: Reject", skeptic.Evidence);
        Assert.Contains("skeptic analysis", skeptic.Evidence);
        Assert.Contains("skeptic speaks", skeptic.Evidence);
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
        ContractCatalog =
        [
            new TradeableContractView
            {
                Contract = new OptionCandidate
                {
                    ContractSymbol = "TEST260904C00100000",
                    Underlying = "TEST",
                    OptionType = "call",
                    Strike = 100m,
                    Expiration = new DateOnly(2026, 9, 4),
                    Quality = QuoteQuality.TwoSided,
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
            string proposalId, StrategyContext market, WarRoomPurpose purpose, PositionUnderReview? position,
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
                        ProfitProbability = 0.6m,
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
            string proposalId, StrategyContext market, WarRoomPurpose purpose, PositionUnderReview? position,
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

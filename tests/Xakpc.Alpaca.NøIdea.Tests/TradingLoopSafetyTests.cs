using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

public sealed class TradingLoopSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrderLifecycle.Open, 1, 0, 0)]
    [InlineData(OrderLifecycle.Filled, 1, 1, 0)]
    [InlineData(OrderLifecycle.Rejected, 0, 0, 1)]
    public async Task ACloseIsSubmittedOnceAndOnlyAFillCountsAsClosed(
        OrderLifecycle lifecycle, int submitted, int closed, int rejected)
    {
        await WithLoopAsync(lifecycle, blocked: false, pendingClose: false, async (loop, gateway, agent) =>
        {
            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Single(gateway.Submitted);
            Assert.Equal(submitted, result.CloseOrdersSubmitted);
            Assert.Equal(closed, result.PositionsClosed);
            Assert.Equal(rejected, result.ActionsRejected);
            Assert.Equal(0, agent.ReviewCalls);
        });
    }

    [Fact]
    public async Task ADeclinedProposalIsCountedAsRejected()
    {
        // The 2026-09-01 run reported "0 rejected" for a cycle that judged a contract and
        // turned it down, which reads as a cycle where nothing happened.
        await WithLoopAsync(
            OrderLifecycle.Open, blocked: false, pendingClose: false,
            async (loop, _, agent) =>
            {
                agent.Decision = StrategyDecision.Declined(
                    "the catalyst is already in the price",
                    new DecisionRejection { Code = "WITHDRAWN_BY_PROPOSER", Stage = "room" },
                    "TSLA260904C00370000",
                    0.43m);

                var result = await loop.RunCycleAsync(CancellationToken.None);

                Assert.Equal(1, result.ActionsRejected);
                Assert.Equal(0, result.OrdersSubmitted);
            },
            hasPosition: false);
    }

    [Fact]
    public async Task APlainHoldIsNotCountedAsRejected()
    {
        // Proposing nothing and declining something must stay distinguishable.
        await WithLoopAsync(
            OrderLifecycle.Open, blocked: false, pendingClose: false,
            async (loop, _, agent) =>
            {
                agent.Decision = StrategyDecision.Nothing("the catalog is empty");

                var result = await loop.RunCycleAsync(CancellationToken.None);

                Assert.Equal(0, result.ActionsRejected);
            },
            hasPosition: false);
    }

    [Fact]
    public async Task AReviewThatDecidesToCloseReachesTheBroker()
    {
        // The position is 5 percent down, so no hard exit fires and the review is the only
        // thing that can close it. This is the path a room that votes against holding uses.
        await WithLoopAsync(
            OrderLifecycle.Filled, blocked: false, pendingClose: false,
            async (loop, gateway, agent) =>
            {
                agent.ReviewDecision = new StrategyDecision
                {
                    Actions =
                    [
                        new StrategyAction
                        {
                            Kind = StrategyActionKind.ClosePosition,
                            ContractSymbol = "SPY260904C00770000",
                            Contracts = 1,
                            Reasoning = "the room voted against holding",
                        },
                    ],
                };

                var result = await loop.RunCycleAsync(CancellationToken.None);

                Assert.Equal(1, agent.ReviewCalls);
                var order = Assert.Single(gateway.Submitted);
                Assert.Equal("SPY260904C00770000", order.ContractSymbol);
                Assert.False(order.IsBuy);
                Assert.Equal(1, result.CloseOrdersSubmitted);
                Assert.Equal(1, result.PositionsClosed);
            },
            positionPrice: 1.9m);
    }

    [Fact]
    public async Task APendingSellSuppressesMandatoryAndWarRoomCloses()
    {
        await WithLoopAsync(OrderLifecycle.Open, blocked: false, pendingClose: true, async (loop, gateway, agent) =>
        {
            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Empty(gateway.Submitted);
            Assert.Equal(0, result.CloseOrdersSubmitted);
            Assert.Equal(0, agent.ReviewCalls);
        });
    }

    [Fact]
    public async Task ABlockedAccountStillAttemptsMandatoryExitBeforeModelWork()
    {
        await WithLoopAsync(OrderLifecycle.Open, blocked: true, pendingClose: false, async (loop, gateway, agent) =>
        {
            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Single(gateway.Submitted);
            Assert.Equal(1, result.CloseOrdersSubmitted);
            Assert.Equal(0, agent.DecideCalls);
            Assert.Equal(0, agent.ReviewCalls);
        });
    }

    [Fact]
    public async Task AFreshAccountUsesCurrentEquityWhenPriorCloseIsMissing()
    {
        await WithLoopAsync(
            OrderLifecycle.Open, blocked: false, pendingClose: false,
            async (loop, _, agent) =>
            {
                await loop.RunCycleAsync(CancellationToken.None);

                Assert.NotNull(agent.LastContext);
                Assert.False(agent.LastContext.NewPositionsHalted);
                Assert.Null(agent.LastContext.NewPositionsHaltReason);
            },
            hasPosition: false,
            previousCloseEquity: null);
    }

    [Fact]
    public async Task AMissingPriorCloseStillFailsClosedAfterAFill()
    {
        await WithLoopAsync(
            OrderLifecycle.Open, blocked: false, pendingClose: false,
            async (loop, _, agent) =>
            {
                await loop.RunCycleAsync(CancellationToken.None);

                Assert.NotNull(agent.LastContext);
                Assert.True(agent.LastContext.NewPositionsHalted);
                Assert.Equal(
                    "prior-close equity is unavailable",
                    agent.LastContext.NewPositionsHaltReason);
            },
            hasPosition: false,
            previousCloseEquity: null,
            hasFillToday: true);
    }

    private static async Task WithLoopAsync(
        OrderLifecycle closeLifecycle,
        bool blocked,
        bool pendingClose,
        Func<TradingLoop, LoopGateway, ReviewingAgent, Task> test,
        bool hasPosition = true,
        decimal? previousCloseEquity = 100_000m,
        bool hasFillToday = false,
        decimal positionPrice = 0.7m)
    {
        var path = Path.Combine(Path.GetTempPath(), $"loop-safety-{Guid.NewGuid():N}.db");
        try
        {
            var store = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await store.CreateSchemaAsync(CancellationToken.None);
            var gateway = new LoopGateway(
                closeLifecycle, blocked, pendingClose, hasPosition,
                previousCloseEquity, hasFillToday, positionPrice);
            var agent = new ReviewingAgent();
            var risk = new RiskOptions();
            var loop = new TradingLoop(
                new EmptyMarketDataGateway(),
                gateway,
                agent,
                new RiskGuard(risk, new FakeClock(Now)),
                risk,
                new TradingOptions { TrackedSymbols = [] },
                store,
                new FakeClock(Now),
                NullLogger.Instance)
            {
                Mode = "live",
            };

            await test(loop, gateway, agent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class ReviewingAgent : IStrategyAgent, IPositionReviewer
    {
        public string Name => "reviewing-test";
        public int DecideCalls { get; private set; }
        public int ReviewCalls { get; private set; }
        public StrategyContext? LastContext { get; private set; }

        /// <summary>What the agent returns, when a test needs something other than a hold.</summary>
        public StrategyDecision? Decision { get; set; }

        /// <summary>What a position review returns. A hold unless a test says otherwise.</summary>
        public StrategyDecision? ReviewDecision { get; set; }

        public PositionState? LastReviewed { get; private set; }

        public Task<StrategyDecision> DecideAsync(
            StrategyContext context, CancellationToken cancellationToken)
        {
            DecideCalls++;
            LastContext = context;
            return Task.FromResult(Decision ?? StrategyDecision.Nothing("test hold"));
        }

        public Task<StrategyDecision> ReviewPositionAsync(
            StrategyContext context,
            PositionState position,
            string triggerReason,
            decimal? unrealizedFraction,
            int? daysToExpiration,
            CancellationToken cancellationToken)
        {
            ReviewCalls++;
            LastReviewed = position;
            return Task.FromResult(ReviewDecision ?? StrategyDecision.Nothing("test hold"));
        }
    }

    private sealed class LoopGateway(
        OrderLifecycle closeLifecycle,
        bool blocked,
        bool hasInitialPendingClose,
        bool hasPosition,
        decimal? previousCloseEquity,
        bool hasFillToday,
        decimal positionPrice) : ITradingGateway
    {
        private readonly List<OrderState> _knownOrders = hasInitialPendingClose
            ? [Order("existing-close", OrderLifecycle.Open)]
            : [];

        public List<OrderRequest> Submitted { get; } = [];

        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccountState
            {
                AccountNumber = "TEST",
                Equity = 100_000m,
                PreviousCloseEquity = previousCloseEquity,
                Cash = 99_900m,
                BuyingPower = 99_900m,
                IsTradingBlocked = blocked,
                IsAccountBlocked = false,
                OptionsTradingLevel = 3,
            });

        public Task<IReadOnlyList<PositionState>> ListPositionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PositionState>>(hasPosition
                ?
                [
                new PositionState
                {
                    Symbol = "SPY260904C00770000",
                    Quantity = 1,
                    AverageEntryPrice = 2m,
                    CurrentPrice = positionPrice,
                },
                ]
                : []);

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>(
                _knownOrders.Where(order => !order.IsTerminal).ToArray());

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>(hasFillToday
                ? [Order("today-fill", OrderLifecycle.Filled)]
                : []);

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderState?>(_knownOrders.FirstOrDefault(order =>
                string.Equals(order.ClientOrderId, clientOrderId, StringComparison.Ordinal)));

        public Task<OrderState> SubmitOrderAsync(
            OrderRequest request, CancellationToken cancellationToken)
        {
            Submitted.Add(request);
            var order = Order(request.ClientOrderId, closeLifecycle);
            _knownOrders.Add(order);
            return Task.FromResult(order);
        }

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static OrderState Order(string clientOrderId, OrderLifecycle lifecycle) => new()
        {
            ClientOrderId = clientOrderId,
            BrokerOrderId = $"broker-{clientOrderId}",
            ContractSymbol = "SPY260904C00770000",
            Lifecycle = lifecycle,
            FilledQuantity = lifecycle == OrderLifecycle.Filled ? 1 : 0,
            RequestedQuantity = 1,
            IsBuy = false,
            RawStatus = lifecycle.ToString(),
        };
    }

    private sealed class EmptyMarketDataGateway : IMarketDataGateway
    {
        public Task<MarketClock> GetClockAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LatestTrade> GetLatestTradeAsync(
            string symbol, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PriceBar>> GetBarsAsync(
            string symbol, string timeframe, DateTimeOffset from, DateTimeOffset to,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PriceBar>>([]);

        public Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
            OptionChainQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OptionCandidate>>([]);

        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(
            IReadOnlyCollection<string> symbols, DateTimeOffset from, DateTimeOffset to,
            int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The deterministic exits run on their own timer, not at the war-room cadence.
/// </summary>
/// <remarks>
/// <para>
/// A stop-loss sampled once every 38 to 41 minutes is a poll, not a stop. On a 0.89 premium
/// contract with delta -0.28, a 40 percent stop is passed by a 0.18 percent move in the
/// underlying, which takes minutes. Alpaca has no stop order type and no bracket order class
/// for options, so nothing broker-side can hold this line.
/// </para>
/// <para>
/// These tests pin two things: an exit pass closes on its own with no cycle and no model, and
/// the broker gate keeps a pass and a cycle from ever sending two sells for one position.
/// </para>
/// </remarks>
public sealed class HardExitLoopTests
{
    private static readonly DateTimeOffset BeforeFlatten = new(2026, 9, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterFlatten = new(2026, 9, 3, 19, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AStopLossClosesWithNoCycleAndNoModel()
    {
        await WithLoopAsync(async (loop, gateway, agent) =>
        {
            await loop.InitializeAsync(CancellationToken.None);

            // Entry 2.00, now 0.70: down 65 percent, past the 60 percent stop.
            var result = await loop.RunHardExitsAsync(CancellationToken.None);

            var sent = Assert.Single(gateway.Submitted);
            Assert.False(sent.IsBuy);
            Assert.Equal(1, result.Submitted);
            Assert.Single(result.AttemptedSymbols);

            // The whole point: no model was consulted to get here.
            Assert.Equal(0, agent.DecideCalls);
            Assert.Equal(0, agent.ReviewCalls);
        });
    }

    [Fact]
    public async Task TheCompetitionFlattenClosesWithNoCycle()
    {
        // Missing the measurement point cannot be recovered from, and at the cycle cadence the
        // flatten instant could be missed by up to 41 minutes.
        await WithLoopAsync(
            async (loop, gateway, _) =>
            {
                await loop.InitializeAsync(CancellationToken.None);
                await loop.RunHardExitsAsync(CancellationToken.None);

                Assert.Single(gateway.Submitted);
            },
            now: AfterFlatten,
            currentPrice: 2m);   // Flat, so only the flatten rule can close this.
    }

    [Fact]
    public async Task APendingCloseStopsASecondSell()
    {
        await WithLoopAsync(
            async (loop, gateway, _) =>
            {
                await loop.InitializeAsync(CancellationToken.None);
                await loop.RunHardExitsAsync(CancellationToken.None);

                Assert.Empty(gateway.Submitted);
            },
            pendingClose: true);
    }

    [Fact]
    public async Task APositionWithNoPriceIsHeldRatherThanClosedBlindly()
    {
        await WithLoopAsync(
            async (loop, gateway, _) =>
            {
                await loop.InitializeAsync(CancellationToken.None);
                await loop.RunHardExitsAsync(CancellationToken.None);

                Assert.Empty(gateway.Submitted);
            },
            currentPrice: null);
    }

    [Fact]
    public async Task AnUninitialisedLoopReportsNoWorkRatherThanRacing()
    {
        // The exit loop can start before the first cycle has initialised the coordinator.
        await WithLoopAsync(async (loop, gateway, _) =>
        {
            var result = await loop.RunHardExitsAsync(CancellationToken.None);

            Assert.Empty(gateway.Submitted);
            Assert.Empty(result.AttemptedSymbols);
        });
    }

    [Fact]
    public async Task NoPositionsMeansNoOrderRead()
    {
        await WithLoopAsync(
            async (loop, gateway, _) =>
            {
                await loop.InitializeAsync(CancellationToken.None);
                var before = gateway.OrderReads;

                await loop.RunHardExitsAsync(CancellationToken.None);

                // An empty account must not cost an order listing every minute.
                Assert.Equal(before, gateway.OrderReads);
            },
            hasPosition: false);
    }

    [Fact]
    public async Task ACycleAndAnExitPassNeverSendTwoSellsForOnePosition()
    {
        // The race the broker gate exists for: both paths read "no pending close" and both act.
        await WithLoopAsync(
            async (loop, gateway, _) =>
            {
                await loop.InitializeAsync(CancellationToken.None);
                gateway.ReadDelay = TimeSpan.FromMilliseconds(40);

                await Task.WhenAll(
                    Task.Run(() => loop.RunCycleAsync(CancellationToken.None)),
                    Task.Run(() => loop.RunHardExitsAsync(CancellationToken.None)));

                Assert.Single(gateway.Submitted);
            });
    }

    [Fact]
    public async Task ASingleCycleStartsNoExitLoop()
    {
        // `--once` is an out-of-hours diagnostic. A second loop that outlives it could reach
        // the broker after the operator believes the run has finished.
        await WithLoopAsync(async (loop, _, _) =>
        {
            var session = new LiveSession(
                loop,
                new SilentMarketDataGateway { Clock = ClosedClock },
                new TradingOptions { TrackedSymbols = [] },
                new RiskOptions(),
                new FakeClock(BeforeFlatten),
                NullLogger.Instance)
            {
                RunOnceIgnoringMarketHours = true,
            };

            await session.RunAsync(CancellationToken.None);

            Assert.Equal(1, session.CyclesRun);
            Assert.Equal(0, session.HardExitPassesRun);
        });
    }

    private static MarketClock ClosedClock => new(
        BeforeFlatten, false, BeforeFlatten.AddHours(18), BeforeFlatten.AddHours(24));

    private static async Task WithLoopAsync(
        Func<TradingLoop, ExitGateway, CountingAgent, Task> test,
        DateTimeOffset? now = null,
        bool hasPosition = true,
        bool pendingClose = false,
        decimal? currentPrice = 0.7m)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hard-exit-{Guid.NewGuid():N}.db");

        try
        {
            var store = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await store.CreateSchemaAsync(CancellationToken.None);

            var clock = new FakeClock(now ?? BeforeFlatten);
            var gateway = new ExitGateway(hasPosition, pendingClose, currentPrice);
            var agent = new CountingAgent();
            var risk = new RiskOptions();
            var loop = new TradingLoop(
                new SilentMarketDataGateway(),
                gateway,
                agent,
                new RiskGuard(risk, clock),
                risk,
                new TradingOptions { TrackedSymbols = [] },
                store,
                clock,
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

    /// <summary>An agent that answers nothing and counts how often it was asked.</summary>
    private sealed class CountingAgent : IStrategyAgent, IPositionReviewer
    {
        public string Name => "counting-test";
        public int DecideCalls { get; private set; }
        public int ReviewCalls { get; private set; }

        public Task<StrategyDecision> DecideAsync(
            StrategyContext context, CancellationToken cancellationToken)
        {
            DecideCalls++;
            return Task.FromResult(StrategyDecision.Nothing("test hold"));
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
            return Task.FromResult(StrategyDecision.Nothing("test hold"));
        }
    }

    /// <summary>
    /// A gateway that drops the position once a sell is sent, so a duplicate close is visible.
    /// </summary>
    /// <remarks>
    /// The lists are deliberately not synchronised. Every path that touches them runs under the
    /// broker gate, so an unsynchronised list is exactly what makes a missing gate show up.
    /// </remarks>
    private sealed class ExitGateway(
        bool hasPosition, bool pendingClose, decimal? currentPrice) : ITradingGateway
    {
        private readonly List<OrderState> _orders = pendingClose
            ? [Sell("existing-close", OrderLifecycle.Open)]
            : [];

        private bool _open = hasPosition;

        public List<OrderRequest> Submitted { get; } = [];

        public int OrderReads { get; private set; }

        /// <summary>Widens the window so a concurrency test actually interleaves.</summary>
        public TimeSpan ReadDelay { get; set; } = TimeSpan.Zero;

        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccountState
            {
                AccountNumber = "TEST",
                Equity = 100_000m,
                PreviousCloseEquity = 100_000m,
                Cash = 99_900m,
                BuyingPower = 99_900m,
                IsTradingBlocked = false,
                IsAccountBlocked = false,
                OptionsTradingLevel = 3,
            });

        public async Task<IReadOnlyList<PositionState>> ListPositionsAsync(
            CancellationToken cancellationToken)
        {
            if (ReadDelay > TimeSpan.Zero)
            {
                await Task.Delay(ReadDelay, cancellationToken);
            }

            return _open
                ?
                [
                    new PositionState
                    {
                        Symbol = "SPY260904C00770000",
                        Quantity = 1,
                        AverageEntryPrice = 2m,
                        CurrentPrice = currentPrice,
                    },
                ]
                : [];
        }

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken)
        {
            OrderReads++;
            return Task.FromResult<IReadOnlyList<OrderState>>(
                _orders.Where(order => !order.IsTerminal).ToArray());
        }

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderState?>(_orders.FirstOrDefault(order =>
                string.Equals(order.ClientOrderId, clientOrderId, StringComparison.Ordinal)));

        public Task<OrderState> SubmitOrderAsync(
            OrderRequest request, CancellationToken cancellationToken)
        {
            Submitted.Add(request);
            var order = Sell(request.ClientOrderId, OrderLifecycle.Open);
            _orders.Add(order);
            _open = false;
            return Task.FromResult(order);
        }

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static OrderState Sell(string clientOrderId, OrderLifecycle lifecycle) => new()
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

    private sealed class SilentMarketDataGateway : IMarketDataGateway
    {
        /// <summary>Set only by the tests that drive a whole session.</summary>
        public MarketClock? Clock { get; init; }

        public Task<MarketClock> GetClockAsync(CancellationToken cancellationToken) =>
            Clock is { } clock
                ? Task.FromResult(clock)
                : throw new NotSupportedException();

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

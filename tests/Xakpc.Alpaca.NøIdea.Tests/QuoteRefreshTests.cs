using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The quote the risk guard judges must be the current one.
/// </summary>
/// <remarks>
/// <para>
/// The catalog is built once at the start of a cycle, and the war room may then debate for
/// longer than <see cref="RiskOptions.MaxQuoteAge"/>. Judging the catalog row at the end of
/// that sitting rejects every otherwise valid trade with a stale quote, and prices the limit
/// order from a quote nobody is offering any more.
/// </para>
/// <para>
/// These tests own the whole open path, which nothing else covered. That absence is why the
/// defect survived a live session.
/// </para>
/// </remarks>
public sealed class QuoteRefreshTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 14, 0, 0, TimeSpan.Zero);
    private const string Symbol = "SPY260904C00500000";

    [Fact]
    public async Task ALongSittingStillOpensWhenTheRefreshedQuoteIsCurrent()
    {
        // The regression test for the 13-minute room deadline against a 10-minute MaxQuoteAge.
        await WithLoopAsync(async (loop, market, trading, agent) =>
        {
            agent.SittingDuration = TimeSpan.FromMinutes(12);

            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Equal(1, result.OrdersSubmitted);
            Assert.Equal(0, result.ActionsRejected);
            Assert.Equal(Symbol, Assert.Single(trading.Submitted).ContractSymbol);
            Assert.Equal(1, market.RefreshReads);
        });
    }

    [Fact]
    public async Task TheLimitPriceComesFromTheRefreshedQuote()
    {
        // The limit was also read from the catalog row, so a moved market was priced at an
        // ask nobody was offering.
        await WithLoopAsync(async (loop, market, trading, agent) =>
        {
            agent.SittingDuration = TimeSpan.FromMinutes(12);
            market.RefreshAsk = 3.40m;

            await loop.RunCycleAsync(CancellationToken.None);

            Assert.Equal(3.40m, Assert.Single(trading.Submitted).LimitPrice);
        });
    }

    [Fact]
    public async Task AFailedRefreshRejectsTheTradeRatherThanUsingTheCatalogQuote()
    {
        await WithLoopAsync(async (loop, market, trading, agent) =>
        {
            agent.SittingDuration = TimeSpan.FromMinutes(12);
            market.FailRefresh = true;

            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Equal(0, result.OrdersSubmitted);
            Assert.Equal(1, result.ActionsRejected);
            Assert.Empty(trading.Submitted);
        });
    }

    [Fact]
    public async Task ARefreshThatFindsNoMatchingContractRejectsTheTrade()
    {
        await WithLoopAsync(async (loop, market, trading, agent) =>
        {
            agent.SittingDuration = TimeSpan.FromMinutes(12);
            market.RefreshReturnsNothing = true;

            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Equal(0, result.OrdersSubmitted);
            Assert.Equal(1, result.ActionsRejected);
            Assert.Empty(trading.Submitted);
        });
    }

    [Fact]
    public async Task ARefreshedQuoteThatIsItselfStaleIsStillRejected()
    {
        // The refresh must not become a way around the stale-quote rule.
        await WithLoopAsync(async (loop, market, trading, agent) =>
        {
            agent.SittingDuration = TimeSpan.FromMinutes(12);
            market.RefreshQuoteAge = TimeSpan.FromMinutes(30);

            var result = await loop.RunCycleAsync(CancellationToken.None);

            Assert.Equal(0, result.OrdersSubmitted);
            Assert.Equal(1, result.ActionsRejected);
            Assert.Empty(trading.Submitted);
        });
    }

    // ---------------------------------------------------------------- harness

    private static async Task WithLoopAsync(
        Func<TradingLoop, ChainMarketDataGateway, OpeningTradingGateway, OpeningAgent, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"quote-refresh-{Guid.NewGuid():N}.db");
        try
        {
            var store = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await store.CreateSchemaAsync(CancellationToken.None);

            var clock = new SteppingClock(Start);
            var market = new ChainMarketDataGateway(clock);
            var trading = new OpeningTradingGateway();
            var agent = new OpeningAgent(clock);
            var risk = new RiskOptions
            {
                CompetitionFlattenUtc = new DateTimeOffset(2026, 9, 3, 19, 30, 0, TimeSpan.Zero),
            };

            var loop = new TradingLoop(
                market,
                trading,
                agent,
                new RiskGuard(risk, clock),
                risk,
                new TradingOptions { TrackedSymbols = ["SPY"] },
                store,
                clock,
                NullLogger.Instance)
            {
                Mode = "live",
            };

            await test(loop, market, trading, agent);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    /// <summary>A clock a test can move forward, to stand for a long sitting.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Opens one known contract, after letting the clock run on.</summary>
    private sealed class OpeningAgent(SteppingClock clock) : IStrategyAgent
    {
        public string Name => "opening-test";

        /// <summary>How long the room is taken to have deliberated.</summary>
        public TimeSpan SittingDuration { get; set; } = TimeSpan.Zero;

        public Task<StrategyDecision> DecideAsync(
            StrategyContext context, CancellationToken cancellationToken)
        {
            // The catalog above was read before this ran. Moving the clock here is what makes
            // its quote stale, exactly as a real sitting does.
            clock.Advance(SittingDuration);

            return Task.FromResult(new StrategyDecision
            {
                Actions =
                [
                    new StrategyAction
                    {
                        Kind = StrategyActionKind.OpenCall,
                        ContractSymbol = Symbol,
                        Contracts = 1,
                        ProfitProbability = 0.6m,
                        Reasoning = "test open",
                    },
                ],
            });
        }
    }

    /// <summary>
    /// Serves one underlying and one contract, and quotes each read at the current clock.
    /// </summary>
    private sealed class ChainMarketDataGateway(SteppingClock clock) : IMarketDataGateway
    {
        /// <summary>Chain reads pinned to a single strike, which is the refresh.</summary>
        public int RefreshReads { get; private set; }

        public bool FailRefresh { get; set; }
        public bool RefreshReturnsNothing { get; set; }
        public decimal RefreshAsk { get; set; } = 3m;

        /// <summary>How old the refreshed quote is said to be.</summary>
        public TimeSpan RefreshQuoteAge { get; set; } = TimeSpan.Zero;

        public Task<MarketClock> GetClockAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LatestTrade> GetLatestTradeAsync(
            string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(new LatestTrade(symbol, 500m, clock.GetUtcNow()));

        public Task<IReadOnlyList<PriceBar>> GetBarsAsync(
            string symbol, string timeframe, DateTimeOffset from, DateTimeOffset to,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PriceBar>>([]);

        public Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
            OptionChainQuery query, CancellationToken cancellationToken)
        {
            // The refresh pins both strike bounds to one value. The catalog read does not.
            var isRefresh = query.StrikeFrom is not null && query.StrikeFrom == query.StrikeTo;

            if (!isRefresh)
            {
                return Task.FromResult<IReadOnlyList<OptionCandidate>>(
                    [Contract(3m, clock.GetUtcNow())]);
            }

            RefreshReads++;

            if (FailRefresh)
            {
                throw new InvalidOperationException("the quote read failed");
            }

            if (RefreshReturnsNothing)
            {
                return Task.FromResult<IReadOnlyList<OptionCandidate>>([]);
            }

            return Task.FromResult<IReadOnlyList<OptionCandidate>>(
                [Contract(RefreshAsk, clock.GetUtcNow() - RefreshQuoteAge)]);
        }

        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(
            IReadOnlyCollection<string> symbols, DateTimeOffset from, DateTimeOffset to,
            int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NewsItem>>([]);

        private static OptionCandidate Contract(decimal ask, DateTimeOffset quotedAt) => new()
        {
            ContractSymbol = Symbol,
            Underlying = "SPY",
            OptionType = "call",
            Strike = 500m,
            Expiration = new DateOnly(2026, 9, 4),
            Quality = QuoteQuality.TwoSided,
            Bid = ask - 0.05m,
            Ask = ask,
            QuoteTimestampUtc = quotedAt,
            Delta = 0.5m,
            ImpliedVolatility = 0.3m,
        };
    }

    private sealed class OpeningTradingGateway : ITradingGateway
    {
        public List<OrderRequest> Submitted { get; } = [];

        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccountState
            {
                AccountNumber = "TEST",
                Equity = 100_000m,
                PreviousCloseEquity = 100_000m,
                Cash = 100_000m,
                BuyingPower = 100_000m,
                IsTradingBlocked = false,
                IsAccountBlocked = false,
                OptionsTradingLevel = 3,
            });

        public Task<IReadOnlyList<PositionState>> ListPositionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PositionState>>([]);

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<OrderState> SubmitOrderAsync(
            OrderRequest request, CancellationToken cancellationToken)
        {
            Submitted.Add(request);
            return Task.FromResult(Filled(request));
        }

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderState?>(
                Submitted.Count == 0
                    ? null
                    : Filled(Submitted[^1]));

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        private static OrderState Filled(OrderRequest request) => new()
        {
            BrokerOrderId = Guid.NewGuid().ToString("N"),
            ClientOrderId = request.ClientOrderId,
            ContractSymbol = request.ContractSymbol,
            Lifecycle = OrderLifecycle.Filled,
            RequestedQuantity = request.Quantity,
            FilledQuantity = request.Quantity,
            LimitPrice = request.LimitPrice,
            AverageFillPrice = request.LimitPrice,
            IsBuy = request.IsBuy,
            RawStatus = "filled",
        };
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

public sealed class AuditFailureTests
{
    [Fact]
    public async Task MandatoryCloseIsAttemptedOnceAndThenAuditFailureStopsTheCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audit-failure-{Guid.NewGuid():N}.db");
        try
        {
            var writable = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await writable.CreateSchemaAsync(CancellationToken.None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var trading = new CloseRecordingTradingGateway();
            var risk = new RiskOptions();
            var loop = new TradingLoop(
                new UnusedMarketDataGateway(),
                trading,
                new UnusedAgent(),
                new RiskGuard(risk, TimeProvider.System),
                risk,
                new TradingOptions(),
                new TradingStore(TradingStore.ConnectionStringForFile(path, readOnly: true)),
                TimeProvider.System,
                NullLogger.Instance)
            {
                Mode = "live",
            };

            await Assert.ThrowsAsync<AuditPersistenceException>(
                () => loop.RunCycleAsync(CancellationToken.None));
            Assert.Equal(1, trading.CloseAttempts);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class CloseRecordingTradingGateway : ITradingGateway
    {
        public int CloseAttempts { get; private set; }

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

        public Task<IReadOnlyList<PositionState>> ListPositionsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PositionState>>(
            [
                new PositionState
                {
                    Symbol = "SPY260904C00770000",
                    Quantity = 1,
                    AverageEntryPrice = 2m,
                    CurrentPrice = 1m,
                },
            ]);

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<OrderState> SubmitOrderAsync(
            OrderRequest request, CancellationToken cancellationToken)
        {
            CloseAttempts++;
            return Task.FromResult(new OrderState
            {
                ClientOrderId = request.ClientOrderId,
                BrokerOrderId = "broker-close",
                ContractSymbol = request.ContractSymbol,
                Lifecycle = OrderLifecycle.Filled,
                FilledQuantity = request.Quantity,
                RequestedQuantity = request.Quantity,
                IsBuy = request.IsBuy,
                RawStatus = "filled",
            });
        }

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<OrderState?>(null);

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedMarketDataGateway : IMarketDataGateway
    {
        public Task<MarketClock> GetClockAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LatestTrade> GetLatestTradeAsync(
            string symbol, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PriceBar>> GetBarsAsync(
            string symbol, string timeframe, DateTimeOffset from, DateTimeOffset to,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<OptionCandidate>> GetOptionCandidatesAsync(
            OptionChainQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(
            IReadOnlyCollection<string> symbols, DateTimeOffset from, DateTimeOffset to,
            int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedAgent : IStrategyAgent
    {
        public string Name => "unused";

        public Task<StrategyDecision> DecideAsync(
            StrategyContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

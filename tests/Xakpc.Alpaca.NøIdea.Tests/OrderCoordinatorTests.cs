using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

public sealed class OrderCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUncertainBuyIsQuarantinedAndNeverReplayed()
    {
        await WithStoreAsync(async store =>
        {
            await ReserveAsync(store, "buy-1", "Buy", 1.25m);
            var gateway = new RecoveryGateway();
            var coordinator = new OrderCoordinator(
                gateway, store, new FakeClock(Now), NullLogger.Instance, "live");

            var pending = await coordinator.ReconcileAndListPendingAsync(
                replayMissingSells: true, CancellationToken.None);

            var order = Assert.Single(pending);
            Assert.True(order.IsBuy);
            Assert.Equal(125m, order.RemainingNotional);
            Assert.Empty(gateway.Submitted);
        });
    }

    [Fact]
    public async Task AnUncertainSellIsReplayedWithTheReservedClientId()
    {
        await WithStoreAsync(async store =>
        {
            await ReserveAsync(store, "sell-1", "Sell", null);
            var gateway = new RecoveryGateway
            {
                SubmitResult = Order("sell-1", OrderLifecycle.Open, isBuy: false),
            };
            var coordinator = new OrderCoordinator(
                gateway, store, new FakeClock(Now), NullLogger.Instance, "live");

            var pending = await coordinator.ReconcileAndListPendingAsync(
                replayMissingSells: true, CancellationToken.None);

            var request = Assert.Single(gateway.Submitted);
            Assert.Equal("sell-1", request.ClientOrderId);
            Assert.False(request.IsBuy);
            Assert.Equal("sell-1", Assert.Single(pending).ClientOrderId);
        });
    }

    [Fact]
    public async Task ReconciliationUpdatesTheOrderAndLinkedCloseDecision()
    {
        await WithStoreAsync(async store =>
        {
            await ReserveAsync(store, "sell-filled", "Sell", null);
            var gateway = new RecoveryGateway
            {
                FindResult = Order("sell-filled", OrderLifecycle.Filled, isBuy: false),
            };
            var coordinator = new OrderCoordinator(
                gateway, store, new FakeClock(Now), NullLogger.Instance, "live");

            var pending = await coordinator.ReconcileAndListPendingAsync(
                replayMissingSells: true, CancellationToken.None);

            Assert.Empty(pending);
            Assert.Equal("Filled", (await store.FindAsync(
                "sell-filled", CancellationToken.None))!.Status);
            Assert.Equal("closed", Assert.Single(await store.RecentDecisionsAsync(
                10, CancellationToken.None)).Outcome);
        });
    }

    [Fact]
    public async Task ARejectedSellReplayStaysQuarantinedForTheCurrentCycle()
    {
        await WithStoreAsync(async store =>
        {
            await ReserveAsync(store, "sell-race", "Sell", null);
            var gateway = new RecoveryGateway
            {
                SubmitResult = Order("sell-race", OrderLifecycle.Rejected, isBuy: false),
            };
            var coordinator = new OrderCoordinator(
                gateway, store, new FakeClock(Now), NullLogger.Instance, "live");

            var pending = await coordinator.ReconcileAndListPendingAsync(
                replayMissingSells: true, CancellationToken.None);

            Assert.Equal(OrderLifecycle.Uncertain, Assert.Single(pending).Lifecycle);
            Assert.Equal("Rejected", (await store.FindAsync(
                "sell-race", CancellationToken.None))!.Status);
        });
    }

    private static async Task ReserveAsync(
        TradingStore store, string clientOrderId, string side, decimal? limitPrice)
    {
        await store.RecordDecisionAndReserveAsync(
            new DecisionEventRow
            {
                TimestampUtc = Now.ToUnixTimeSeconds(),
                Mode = "live",
                Purpose = side == "Sell" ? "mandatory-exit" : "new-trade",
                Action = side == "Sell" ? "ClosePosition" : "OpenCall",
                Outcome = "accepted",
                OptionSymbol = "SPY260904C00770000",
            },
            new OrderRecord
            {
                CorrelationId = clientOrderId,
                ClientOrderId = clientOrderId,
                OptionSymbol = "SPY260904C00770000",
                Side = side,
                Quantity = 1,
                OrderType = limitPrice is null ? "Market" : "Limit",
                LimitPrice = limitPrice,
                SubmittedUtc = Now.ToUnixTimeSeconds(),
                Status = OrderLifecycle.Reserved.ToString(),
                Mode = "live",
            },
            CancellationToken.None);
    }

    private static OrderState Order(string clientOrderId, OrderLifecycle lifecycle, bool isBuy) => new()
    {
        ClientOrderId = clientOrderId,
        BrokerOrderId = $"broker-{clientOrderId}",
        ContractSymbol = "SPY260904C00770000",
        Lifecycle = lifecycle,
        FilledQuantity = lifecycle == OrderLifecycle.Filled ? 1 : 0,
        RequestedQuantity = 1,
        IsBuy = isBuy,
        LimitPrice = isBuy ? 1.25m : null,
        RawStatus = lifecycle.ToString(),
    };

    private static async Task WithStoreAsync(Func<TradingStore, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"coordinator-{Guid.NewGuid():N}.db");
        try
        {
            var store = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await store.CreateSchemaAsync(CancellationToken.None);
            await test(store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class RecoveryGateway : ITradingGateway
    {
        public List<OrderRequest> Submitted { get; } = [];
        public OrderState? FindResult { get; init; }
        public OrderState? SubmitResult { get; init; }

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(FindResult);

        public Task<OrderState> SubmitOrderAsync(
            OrderRequest request, CancellationToken cancellationToken)
        {
            Submitted.Add(request);
            return Task.FromResult(SubmitResult ?? throw new InvalidOperationException("No submit result."));
        }

        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderState>>([]);

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

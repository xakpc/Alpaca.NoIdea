using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The dry run does everything except send.
/// </summary>
/// <remarks>
/// The guarantee is structural: the gateway handed to the loop has no path to the inner one
/// for any write. These tests pin that the reads still go through — a dry run against a fake
/// account would prove nothing — and that no write ever does.
/// </remarks>
public class DryRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 18, 0, 0, TimeSpan.Zero);

    private static DryRunTradingGateway Wrap(RecordingGateway inner) =>
        new(inner, new FakeClock(Now), NullLogger.Instance);

    [Fact]
    public async Task ReadsGoThroughToTheRealAccount()
    {
        // A dry run must exercise the true account and true prices. Only the writes differ.
        var inner = new RecordingGateway();
        var gateway = Wrap(inner);

        var account = await gateway.GetAccountAsync(CancellationToken.None);
        await gateway.ListPositionsAsync(CancellationToken.None);
        await gateway.ListOpenOrdersAsync(CancellationToken.None);
        await gateway.ListOrdersSinceAsync(Now.AddDays(-1), CancellationToken.None);
        await gateway.FindOrderByClientIdAsync("x", CancellationToken.None);

        Assert.Equal("REAL", account.AccountNumber);
        Assert.Equal(5, inner.Reads);
    }

    [Fact]
    public async Task SubmitNeverReachesTheBroker()
    {
        var inner = new RecordingGateway();
        var gateway = Wrap(inner);

        var order = await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "dry-1",
                ContractSymbol = "TEST260904C00100000",
                Quantity = 2,
                IsBuy = true,
                LimitPrice = 1.50m,
            },
            CancellationToken.None);

        Assert.Equal(0, inner.Writes);
        Assert.Equal("dry_run_not_sent", order.RawStatus);

        // Reported as accepted so the cycle continues exactly as it would live.
        Assert.Equal(OrderLifecycle.Open, order.Lifecycle);
        Assert.Equal("dry-1", order.ClientOrderId);
    }

    [Fact]
    public async Task ClosingAndCancellingNeverReachTheBroker()
    {
        var inner = new RecordingGateway();
        var gateway = Wrap(inner);

        await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "dry-close",
                ContractSymbol = "TEST260904C00100000",
                Quantity = 1,
                IsBuy = false,
            }, CancellationToken.None);
        await gateway.CancelOrderAsync("some-broker-id", CancellationToken.None);

        Assert.Equal(0, inner.Writes);
    }

    [Fact]
    public async Task EveryDecidedOrderIsRecordedWithItsNotional()
    {
        var gateway = Wrap(new RecordingGateway());

        await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "dry-1",
                ContractSymbol = "TEST260904C00100000",
                Quantity = 2,
                IsBuy = true,
                LimitPrice = 1.50m,
            },
            CancellationToken.None);

        var planned = Assert.Single(gateway.Planned);
        Assert.Equal("TEST260904C00100000", planned.ContractSymbol);
        Assert.Equal(2, planned.Contracts);

        // 1.50 x 2 contracts x 100 multiplier.
        Assert.Equal(300m, planned.Notional);
        Assert.Equal(300m, gateway.PlannedNotional);
    }

    [Fact]
    public async Task ASellDoesNotCountTowardCommittedPremium()
    {
        // Only buying commits premium. Counting a close as exposure would double-count.
        var gateway = Wrap(new RecordingGateway());

        await gateway.SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = "dry-sell",
                ContractSymbol = "TEST260904C00100000",
                Quantity = 1,
                IsBuy = false,
                LimitPrice = 2.00m,
            },
            CancellationToken.None);

        Assert.Single(gateway.Planned);
        Assert.Equal(0m, gateway.PlannedNotional);
    }

    /// <summary>A gateway that counts what it was asked to do.</summary>
    private sealed class RecordingGateway : ITradingGateway
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new AccountState
            {
                AccountNumber = "REAL",
                Equity = 100_000m,
                Cash = 100_000m,
                BuyingPower = 100_000m,
                IsTradingBlocked = false,
                IsAccountBlocked = false,
            });
        }

        public Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<PositionState>>([]);
        }

        public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<OrderState>>([]);
        }

        public Task<OrderState?> FindOrderByClientIdAsync(
            string clientOrderId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<OrderState?>(null);
        }

        public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
            DateTimeOffset fromUtc, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<OrderState>>([]);
        }

        public Task<OrderState> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken)
        {
            Writes++;
            throw new InvalidOperationException("A dry run must never reach the broker.");
        }

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
        {
            Writes++;
            throw new InvalidOperationException("A dry run must never reach the broker.");
        }

    }
}

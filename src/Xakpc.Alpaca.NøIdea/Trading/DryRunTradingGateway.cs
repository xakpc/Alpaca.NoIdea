using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>An order the system decided to place, and did not place.</summary>
public sealed record PlannedOrder(
    DateTimeOffset AtUtc,
    string ClientOrderId,
    string ContractSymbol,
    int Contracts,
    bool IsBuy,
    decimal? LimitPrice,
    string Kind)
{
    public decimal? Notional => LimitPrice * Contracts * 100m;
}

/// <summary>
/// Everything a real trading gateway does, except send an order.
/// </summary>
/// <remarks>
/// <para>
/// Reads pass straight through to the real gateway, so the account, the positions and the
/// orders are genuine. Only the four write methods are intercepted. That is the point: a dry
/// run should exercise the true account state and the true prices, and differ from a live run
/// in exactly one respect.
/// </para>
/// <para>
/// <b>A decorator rather than a flag inside the loop.</b> A flag can be forgotten at one call
/// site; a gateway that has no way to submit cannot be bypassed by code that forgets to check.
/// The loop is handed this object and cannot tell the difference.
/// </para>
/// <para>
/// This matters most when the market is shut. Submitting into a closed session can leave a
/// resting order that fills at the next open, so a dry run is the only safe way to exercise
/// the full live path out of hours.
/// </para>
/// </remarks>
public sealed class DryRunTradingGateway(
    ITradingGateway inner,
    TimeProvider time,
    ILogger logger) : ITradingGateway
{
    private readonly ITradingGateway _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly List<PlannedOrder> _planned = [];
    private readonly Lock _gate = new();

    /// <summary>Everything that would have been sent, in order.</summary>
    public IReadOnlyList<PlannedOrder> Planned
    {
        get { lock (_gate) { return [.. _planned]; } }
    }

    /// <summary>The total premium the run would have committed.</summary>
    public decimal PlannedNotional
    {
        get { lock (_gate) { return _planned.Where(o => o.IsBuy).Sum(o => o.Notional ?? 0m); } }
    }

    // ---------------------------------------------------------------- reads pass through

    public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken) =>
        _inner.GetAccountAsync(cancellationToken);

    public Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken) =>
        _inner.ListPositionsAsync(cancellationToken);

    public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
        _inner.ListOpenOrdersAsync(cancellationToken);

    public Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
        DateTimeOffset fromUtc, CancellationToken cancellationToken) =>
        _inner.ListOrdersSinceAsync(fromUtc, cancellationToken);

    public Task<OrderState?> FindOrderByClientIdAsync(
        string clientOrderId, CancellationToken cancellationToken) =>
        _inner.FindOrderByClientIdAsync(clientOrderId, cancellationToken);

    // ---------------------------------------------------------------- writes are intercepted

    public Task<OrderState> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Record(new PlannedOrder(
            _time.GetUtcNow(), request.ClientOrderId, request.ContractSymbol,
            request.Quantity, request.IsBuy, request.LimitPrice,
            request.IsBuy ? "open" : "close"));

        _logger.LogWarning(
            "DRY RUN: would {Side} {Contracts}x {Symbol} at {Limit:N2} ({Notional:N2} USD). Not sent.",
            request.IsBuy ? "BUY" : "SELL", request.Quantity, request.ContractSymbol,
            request.LimitPrice, request.LimitPrice * request.Quantity * 100m);

        // Reported as accepted so the cycle continues exactly as it would live: the order is
        // recorded, the daily count advances, and the next cycle sees the same state machine.
        return Task.FromResult(new OrderState
        {
            ClientOrderId = request.ClientOrderId,
            BrokerOrderId = $"dryrun-{_planned.Count}",
            ContractSymbol = request.ContractSymbol,
            Lifecycle = OrderLifecycle.Open,
            FilledQuantity = 0,
            RawStatus = "dry_run_not_sent",
        });
    }

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken)
    {
        _logger.LogWarning("DRY RUN: would cancel {OrderId}. Not sent.", brokerOrderId);
        return Task.CompletedTask;
    }

    private void Record(PlannedOrder order)
    {
        lock (_gate)
        {
            _planned.Add(order);
        }
    }
}

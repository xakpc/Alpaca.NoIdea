using Alpaca.Markets;

namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// <see cref="ITradingGateway"/> over the typed Alpaca SDK clients, on the paper environment.
/// </summary>
/// <remarks>
/// This is the only write path in the system. It is reachable from deterministic C# alone:
/// no MCP server this host runs holds an order tool, so an LLM cannot call it even by
/// mistake (ADR-001, ADR-006).
/// </remarks>
public sealed class LiveTradingGateway(AlpacaClients clients) : ITradingGateway
{
    private readonly AlpacaClients _clients = clients ?? throw new ArgumentNullException(nameof(clients));

    public async Task<AccountState> GetAccountAsync(CancellationToken cancellationToken)
    {
        var account = await _clients.Trading.GetAccountAsync(cancellationToken);

        return new AccountState
        {
            AccountNumber = account.AccountNumber ?? "",
            Equity = account.Equity ?? 0m,
            PreviousCloseEquity = account.LastEquity,
            Cash = account.TradableCash,
            BuyingPower = account.BuyingPower ?? 0m,
            IsTradingBlocked = account.IsTradingBlocked,
            IsAccountBlocked = account.IsAccountBlocked,
            OptionsTradingLevel = account.OptionsTradingLevel is { } level ? (int)level : null,
        };
    }

    public async Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken)
    {
        var positions = await _clients.Trading.ListPositionsAsync(cancellationToken);

        return positions
            .Select(position => new PositionState
            {
                Symbol = position.Symbol,
                Quantity = (int)position.IntegerQuantity,
                AverageEntryPrice = position.AverageEntryPrice,
                CurrentPrice = position.AssetCurrentPrice,
                MarketValue = position.MarketValue,
                UnrealizedPnl = position.UnrealizedProfitLoss,
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var orders = await _clients.Trading.ListOrdersAsync(
            new ListOrdersRequest { OrderStatusFilter = OrderStatusFilter.Open }, cancellationToken);

        return orders.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
        DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var request = new ListOrdersRequest
        {
            OrderStatusFilter = OrderStatusFilter.All,
            LimitOrderNumber = 500,
        }.WithInterval(fromUtc.UtcDateTime.GetIntervalFromThat());
        var orders = await _clients.Trading.ListOrdersAsync(request, cancellationToken);

        return orders.Select(Map).ToArray();
    }

    public async Task<OrderState> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var order = new NewOrderRequest(
            request.ContractSymbol,
            OrderQuantity.FromInt64(request.Quantity),
            request.IsBuy ? OrderSide.Buy : OrderSide.Sell,
            request.LimitPrice is null ? OrderType.Market : OrderType.Limit,
            TimeInForce.Day)
        {
            ClientOrderId = request.ClientOrderId,
            LimitPrice = request.LimitPrice,
        };

        return Map(await _clients.Trading.PostOrderAsync(order, cancellationToken));
    }

    public async Task<OrderState?> FindOrderByClientIdAsync(
        string clientOrderId, CancellationToken cancellationToken)
    {
        try
        {
            return Map(await _clients.Trading.GetOrderAsync(clientOrderId, cancellationToken));
        }
        catch (RestClientErrorException error) when (error.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The reservation exists locally but the broker never saw the order. The caller
            // decides whether to submit; this is not a fault.
            return null;
        }
    }

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
        _clients.Trading.CancelOrderAsync(Guid.Parse(brokerOrderId), cancellationToken);

    private static OrderState Map(IOrder order) => new()
    {
        ClientOrderId = order.ClientOrderId ?? "",
        BrokerOrderId = order.OrderId.ToString(),
        ContractSymbol = order.Symbol,
        Lifecycle = MapLifecycle(order.OrderStatus),
        FilledQuantity = (int)order.IntegerFilledQuantity,
        RequestedQuantity = (int)order.IntegerQuantity,
        IsBuy = order.OrderSide == OrderSide.Buy,
        LimitPrice = order.LimitPrice,
        SubmittedUtc = order.SubmittedAtUtc,
        AverageFillPrice = order.AverageFillPrice,
        RawStatus = order.OrderStatus.ToString(),
    };

    /// <summary>
    /// Collapses the broker status set to the six states the loop reasons about. Anything
    /// unrecognised maps to <see cref="OrderLifecycle.Open"/>, because treating an unknown
    /// status as terminal would let the loop abandon a live order.
    /// </summary>
    private static OrderLifecycle MapLifecycle(OrderStatus status) => status switch
    {
        OrderStatus.Filled => OrderLifecycle.Filled,
        OrderStatus.PartiallyFilled => OrderLifecycle.PartiallyFilled,
        OrderStatus.Canceled => OrderLifecycle.Canceled,
        OrderStatus.Expired => OrderLifecycle.Expired,
        OrderStatus.Rejected => OrderLifecycle.Rejected,
        OrderStatus.Suspended => OrderLifecycle.Rejected,
        OrderStatus.Stopped => OrderLifecycle.Canceled,
        _ => OrderLifecycle.Open,
    };
}

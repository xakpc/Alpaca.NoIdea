using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>
/// <see cref="ITradingGateway"/> that simulates an account against cached history.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class never sends an order.</b> It holds no Alpaca client and opens no network
/// connection, so it cannot reach a broker even if a defect tried to. That is the property
/// the replay tests assert, and it is stronger than a flag that code could forget to check.
/// </para>
/// <para>
/// Fills are optimistic and immediate at the candidate reference price, which is a daily
/// close. Replay P&amp;L is therefore only as accurate as the stored daily option data: there
/// is no bid, no ask, and no intraday path, so a real fill would pay the spread this
/// simulation does not. Treat replay P&amp;L as evidence about the <em>logic</em>, never as a
/// forecast of live P&amp;L. See <c>.lode/replay/replay-mode.md</c>.
/// </para>
/// </remarks>
public sealed class ReplayTradingGateway : ITradingGateway
{
    private const int ContractMultiplier = 100;

    private readonly ReplayClock _clock;
    private readonly Func<string, CancellationToken, Task<decimal?>> _priceLookup;
    private readonly Dictionary<string, OrderState> _ordersByClientId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimulatedPosition> _positions = new(StringComparer.Ordinal);

    private decimal _cash;
    private decimal _realizedPnl;

    /// <param name="clock">The replay clock, so fills are stamped at the replay instant.</param>
    /// <param name="startingEquity">
    /// The opening cash. The official competition account starts at $100,000, so replay uses
    /// the same figure by default and a scaled-down result stays comparable.
    /// </param>
    /// <param name="priceLookup">
    /// Answers the current price of a contract at the replay instant, or null when the cache
    /// has none. A null price makes a close fail closed rather than book an invented number.
    /// </param>
    public ReplayTradingGateway(
        ReplayClock clock,
        decimal startingEquity,
        Func<string, CancellationToken, Task<decimal?>> priceLookup)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _priceLookup = priceLookup ?? throw new ArgumentNullException(nameof(priceLookup));
        _cash = startingEquity;
        StartingEquity = startingEquity;
    }

    public decimal StartingEquity { get; }

    /// <summary>Every simulated fill, in order, for the calibration report.</summary>
    public List<SimulatedFill> Fills { get; } = [];

    public async Task<AccountState> GetAccountAsync(CancellationToken cancellationToken)
    {
        var positionValue = 0m;
        foreach (var position in _positions.Values)
        {
            var price = await _priceLookup(position.ContractSymbol, cancellationToken);
            positionValue += (price ?? position.AverageEntryPrice) * position.Quantity * ContractMultiplier;
        }

        return new AccountState
        {
            AccountNumber = "REPLAY",
            Equity = _cash + positionValue,
            Cash = _cash,
            BuyingPower = _cash,
            IsTradingBlocked = false,
            IsAccountBlocked = false,
            OptionsTradingLevel = 3,
        };
    }

    public async Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken)
    {
        var states = new List<PositionState>(_positions.Count);

        foreach (var position in _positions.Values)
        {
            var price = await _priceLookup(position.ContractSymbol, cancellationToken);
            var current = price ?? position.AverageEntryPrice;

            states.Add(new PositionState
            {
                Symbol = position.ContractSymbol,
                Quantity = position.Quantity,
                AverageEntryPrice = position.AverageEntryPrice,
                CurrentPrice = price,
                MarketValue = current * position.Quantity * ContractMultiplier,
                UnrealizedPnl = (current - position.AverageEntryPrice) * position.Quantity * ContractMultiplier,
            });
        }

        return states;
    }

    /// <summary>Nothing rests: every simulated order fills or fails at once.</summary>
    public Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderState>>([]);

    public async Task<OrderState> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same idempotency contract the live gateway has. A repeated client order id
        // resolves the first order instead of booking a second fill.
        if (_ordersByClientId.TryGetValue(request.ClientOrderId, out var existing))
        {
            return existing;
        }

        var price = await _priceLookup(request.ContractSymbol, cancellationToken);

        if (price is not { } fillPrice || fillPrice <= 0m)
        {
            // Fail closed: no cached price at this instant means the trade cannot be
            // evaluated, so it is rejected rather than filled at a guess.
            var rejected = new OrderState
            {
                ClientOrderId = request.ClientOrderId,
                BrokerOrderId = null,
                ContractSymbol = request.ContractSymbol,
                Lifecycle = OrderLifecycle.Rejected,
                FilledQuantity = 0,
                RawStatus = "replay_no_price",
            };

            _ordersByClientId[request.ClientOrderId] = rejected;
            return rejected;
        }

        var notional = fillPrice * request.Quantity * ContractMultiplier;

        if (request.IsBuy)
        {
            if (notional > _cash)
            {
                var rejected = new OrderState
                {
                    ClientOrderId = request.ClientOrderId,
                    BrokerOrderId = null,
                    ContractSymbol = request.ContractSymbol,
                    Lifecycle = OrderLifecycle.Rejected,
                    FilledQuantity = 0,
                    RawStatus = "replay_insufficient_cash",
                };

                _ordersByClientId[request.ClientOrderId] = rejected;
                return rejected;
            }

            _cash -= notional;
            AddToPosition(request.ContractSymbol, request.Quantity, fillPrice);
        }
        else
        {
            _cash += notional;
            ReducePosition(request.ContractSymbol, request.Quantity, fillPrice);
        }

        Fills.Add(new SimulatedFill(
            _clock.UtcNow, request.ContractSymbol, request.IsBuy, request.Quantity, fillPrice));

        var filled = new OrderState
        {
            ClientOrderId = request.ClientOrderId,
            BrokerOrderId = $"replay-{_ordersByClientId.Count + 1}",
            ContractSymbol = request.ContractSymbol,
            Lifecycle = OrderLifecycle.Filled,
            FilledQuantity = request.Quantity,
            AverageFillPrice = fillPrice,
            RawStatus = "filled",
        };

        _ordersByClientId[request.ClientOrderId] = filled;
        return filled;
    }

    public Task<OrderState?> FindOrderByClientIdAsync(
        string clientOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(_ordersByClientId.GetValueOrDefault(clientOrderId));

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<OrderState> ClosePositionAsync(
        string contractSymbol, CancellationToken cancellationToken)
    {
        if (!_positions.TryGetValue(contractSymbol, out var position))
        {
            return new OrderState
            {
                ClientOrderId = "",
                ContractSymbol = contractSymbol,
                Lifecycle = OrderLifecycle.Rejected,
                FilledQuantity = 0,
                RawStatus = "replay_no_position",
            };
        }

        return await SubmitOrderAsync(
            new OrderRequest
            {
                ClientOrderId = $"replay-close-{contractSymbol}-{_clock.UtcNow.ToUnixTimeSeconds()}",
                ContractSymbol = contractSymbol,
                Quantity = position.Quantity,
                IsBuy = false,
            },
            cancellationToken);
    }

    /// <summary>Realized profit and loss so far, in dollars.</summary>
    public decimal RealizedPnl => _realizedPnl;

    private void AddToPosition(string contractSymbol, int quantity, decimal price)
    {
        if (_positions.TryGetValue(contractSymbol, out var existing))
        {
            var total = existing.Quantity + quantity;
            var weighted =
                ((existing.AverageEntryPrice * existing.Quantity) + (price * quantity)) / total;

            _positions[contractSymbol] = existing with { Quantity = total, AverageEntryPrice = weighted };
            return;
        }

        _positions[contractSymbol] = new SimulatedPosition(contractSymbol, quantity, price);
    }

    private void ReducePosition(string contractSymbol, int quantity, decimal price)
    {
        if (!_positions.TryGetValue(contractSymbol, out var existing))
        {
            return;
        }

        _realizedPnl += (price - existing.AverageEntryPrice) * quantity * ContractMultiplier;

        var remaining = existing.Quantity - quantity;
        if (remaining <= 0)
        {
            _positions.Remove(contractSymbol);
            return;
        }

        _positions[contractSymbol] = existing with { Quantity = remaining };
    }

    private sealed record SimulatedPosition(string ContractSymbol, int Quantity, decimal AverageEntryPrice);
}

/// <summary>One simulated fill, for the calibration report and the audit trail.</summary>
public sealed record SimulatedFill(
    DateTimeOffset AtUtc,
    string ContractSymbol,
    bool IsBuy,
    int Quantity,
    decimal Price);

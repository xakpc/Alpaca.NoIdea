namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>The account as the broker reports it. Alpaca is the source of truth, not SQLite.</summary>
public sealed record AccountState
{
    public required string AccountNumber { get; init; }
    public required decimal Equity { get; init; }
    public required decimal Cash { get; init; }
    public required decimal BuyingPower { get; init; }
    public required bool IsTradingBlocked { get; init; }
    public required bool IsAccountBlocked { get; init; }

    /// <summary>Null or zero means the account cannot buy a long call. Fail closed on it.</summary>
    public int? OptionsTradingLevel { get; init; }
}

/// <summary>One open position.</summary>
public sealed record PositionState
{
    public required string Symbol { get; init; }
    public required int Quantity { get; init; }
    public required decimal AverageEntryPrice { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? MarketValue { get; init; }
    public decimal? UnrealizedPnl { get; init; }
}

/// <summary>The lifecycle of a submitted order, reduced to what the loop reasons about.</summary>
public enum OrderLifecycle
{
    /// <summary>Written to SQLite, not yet sent. The idempotency reservation.</summary>
    Reserved = 0,
    Open = 1,
    Filled = 2,
    PartiallyFilled = 3,
    Canceled = 4,
    Expired = 5,
    Rejected = 6,
}

/// <summary>One order as the broker reports it.</summary>
public sealed record OrderState
{
    public required string ClientOrderId { get; init; }
    public string? BrokerOrderId { get; init; }
    public required string ContractSymbol { get; init; }
    public required OrderLifecycle Lifecycle { get; init; }
    public required int FilledQuantity { get; init; }
    public int RequestedQuantity { get; init; }
    public bool IsBuy { get; init; }
    public decimal? LimitPrice { get; init; }
    public DateTimeOffset? SubmittedUtc { get; init; }
    public decimal? AverageFillPrice { get; init; }

    /// <summary>The broker status verbatim, for the audit trail.</summary>
    public required string RawStatus { get; init; }

    public bool IsTerminal =>
        Lifecycle is OrderLifecycle.Filled or OrderLifecycle.Canceled
            or OrderLifecycle.Expired or OrderLifecycle.Rejected;

    public int RemainingQuantity => Math.Max(0, RequestedQuantity - FilledQuantity);

    public decimal? RemainingNotional => IsBuy && LimitPrice is { } limit
        ? limit * RemainingQuantity * 100m
        : IsBuy ? null : 0m;
}

/// <summary>A request to buy or sell one option contract.</summary>
/// <remarks>
/// <see cref="ClientOrderId"/> is required and carries the idempotency guarantee: the caller
/// reserves it in SQLite before submitting, so an uncertain retry resolves the existing order
/// instead of sending a second one.
/// </remarks>
public sealed record OrderRequest
{
    public required string ClientOrderId { get; init; }
    public required string ContractSymbol { get; init; }
    public required int Quantity { get; init; }
    public required bool IsBuy { get; init; }

    /// <summary>Null submits at market. A limit is the default everywhere in this system.</summary>
    public decimal? LimitPrice { get; init; }
}

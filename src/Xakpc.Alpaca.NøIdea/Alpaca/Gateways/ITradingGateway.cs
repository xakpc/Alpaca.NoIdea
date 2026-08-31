namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// Everything that touches the account or moves money. Only deterministic C# holds one of
/// these; no LLM agent ever receives it (ADR-005, ADR-006).
/// </summary>
/// <remarks>The interface keeps broker writes behind one narrow, testable boundary.</remarks>
public interface ITradingGateway
{
    Task<AccountState> GetAccountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken);

    /// <summary>Open orders only. Restart recovery reconciles against these.</summary>
    Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken);

    /// <summary>All broker orders submitted after the supplied UTC instant.</summary>
    Task<IReadOnlyList<OrderState>> ListOrdersSinceAsync(
        DateTimeOffset fromUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Submits one option order. The caller must already have reserved
    /// <see cref="OrderRequest.ClientOrderId"/> in SQLite.
    /// </summary>
    Task<OrderState> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Looks an order up by <b>client</b> order id, never by broker id. This is the lookup
    /// the recovery path depends on: after an uncertain submit the client id is the only
    /// identifier the application is sure it owns.
    /// </summary>
    Task<OrderState?> FindOrderByClientIdAsync(string clientOrderId, CancellationToken cancellationToken);

    Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken);

}

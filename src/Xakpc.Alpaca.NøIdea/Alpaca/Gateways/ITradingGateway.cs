namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// Everything that touches the account or moves money. Only deterministic C# holds one of
/// these; no LLM agent ever receives it (ADR-005, ADR-006).
/// </summary>
/// <remarks>
/// The replay implementation simulates every write and <b>never sends an order</b>. That is
/// the property the replay tests assert, and it is why the interface exists rather than the
/// loop calling the SDK directly.
/// </remarks>
public interface ITradingGateway
{
    Task<AccountState> GetAccountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PositionState>> ListPositionsAsync(CancellationToken cancellationToken);

    /// <summary>Open orders only. Restart recovery reconciles against these.</summary>
    Task<IReadOnlyList<OrderState>> ListOpenOrdersAsync(CancellationToken cancellationToken);

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

    /// <summary>Closes the whole position in one contract.</summary>
    Task<OrderState> ClosePositionAsync(string contractSymbol, CancellationToken cancellationToken);
}

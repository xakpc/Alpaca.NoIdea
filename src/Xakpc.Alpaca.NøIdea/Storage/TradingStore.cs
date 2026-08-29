using Dapper;
using Microsoft.Data.Sqlite;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>One recorded order attempt.</summary>
/// <remarks>
/// Init-only properties, not a positional record: SQLite returns REAL as
/// double and INTEGER as long, and Dapper converts those per property setter
/// but not through strict constructor matching.
/// </remarks>
public sealed record OrderRecord
{
    public string ClientOrderId { get; init; } = "";
    public string? AlpacaOrderId { get; init; }
    public string OptionSymbol { get; init; } = "";
    public string Side { get; init; } = "";
    public int Quantity { get; init; }
    public string OrderType { get; init; } = "";
    public decimal? LimitPrice { get; init; }
    public long SubmittedUtc { get; init; }
    public string Status { get; init; } = "";
}

/// <summary>
/// The audit trail. This is the only type that contains SQL.
/// </summary>
/// <remarks>
/// The order row is written <em>before</em> the order is submitted. If the submit
/// call then fails with an uncertain result, the client order id is already durable,
/// so the recovery path can ask Alpaca what happened instead of sending a second
/// order. The <c>UNIQUE</c> constraint on <c>client_order_id</c> enforces this.
/// </remarks>
public sealed class TradingStore(string connectionString)
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    public static string ConnectionStringForFile(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Storage", "Schema.sql"), cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Records the intent to submit an order. Call this before the submit, never after.
    /// </summary>
    public async Task ReserveAsync(OrderRecord order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        const string sql =
            """
            INSERT INTO orders (
                client_order_id, option_symbol, side, quantity,
                order_type, limit_price, submitted_utc, status)
            VALUES (
                @ClientOrderId, @OptionSymbol, @Side, @Quantity,
                @OrderType, @LimitPrice, @SubmittedUtc, @Status)
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, order, cancellationToken: cancellationToken));
    }

    /// <summary>Records what Alpaca did with a reserved order.</summary>
    public async Task RecordResultAsync(
        string clientOrderId,
        string? alpacaOrderId,
        string status,
        long? closedUtc,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE orders
            SET alpaca_order_id = @alpacaOrderId,
                status          = @status,
                closed_utc      = COALESCE(@closedUtc, closed_utc)
            WHERE client_order_id = @clientOrderId
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { clientOrderId, alpacaOrderId, status, closedUtc },
            cancellationToken: cancellationToken));
    }

    /// <summary>The reserved order with this client id, or null.</summary>
    public async Task<OrderRecord?> FindAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT client_order_id AS ClientOrderId,
                   alpaca_order_id AS AlpacaOrderId,
                   option_symbol   AS OptionSymbol,
                   side            AS Side,
                   quantity        AS Quantity,
                   order_type      AS OrderType,
                   limit_price     AS LimitPrice,
                   submitted_utc   AS SubmittedUtc,
                   status          AS Status
            FROM orders
            WHERE client_order_id = @clientOrderId
            """;

        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OrderRecord>(new CommandDefinition(
            sql, new { clientOrderId }, cancellationToken: cancellationToken));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}

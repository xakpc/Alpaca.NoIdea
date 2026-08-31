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

    /// <summary>
    /// Which run wrote this. A dry-run order must never be read back as a live one.
    /// </summary>
    public string Mode { get; init; } = "live";

    /// <summary>
    /// The decision this order came from, or null for the <c>--smoke</c> operator check.
    /// </summary>
    /// <remarks>
    /// Null means exactly that: an operator check, not an agent decision. Inventing a
    /// decisions row to satisfy a constraint would put a decision nobody made into the audit
    /// trail, which is why the column carries no foreign key.
    /// </remarks>
    public long? DecisionId { get; init; }
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
public sealed partial class TradingStore(string connectionString)
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    public static string ConnectionStringForFile(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    /// <summary>
    /// The audit tables whose shape changed when the war room replaced the weighted
    /// combiner. Nothing ever wrote them, so an empty one can be rebuilt safely.
    /// </summary>
    private static readonly string[] ReshapedAuditTables =
        ["decisions", "forecasts", "agent_tool_calls", "evaluation_runs"];

    public async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Storage", "Schema.sql"), cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);

        await DropEmptyReshapedTablesAsync(connection, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Rebuilds the audit tables that changed shape, and only while they hold no rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CREATE TABLE IF NOT EXISTS</c> cannot widen a column, and SQLite cannot drop a
    /// NOT NULL with <c>ALTER</c>. These four tables were written by nothing, so dropping an
    /// empty one loses no history and the next statement recreates it correctly.
    /// </para>
    /// <para>
    /// <b>A table with rows in it is left alone</b>, whatever its shape. Silently deleting an
    /// audit trail to fit a schema change is the one outcome this must never have.
    /// <c>orders</c> and <c>equity_snapshots</c> are not in the list at all: they carry real
    /// history and their shape did not change.
    /// </para>
    /// </remarks>
    private static async Task DropEmptyReshapedTablesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in ReshapedAuditTables)
        {
            var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table",
                new { table },
                cancellationToken: cancellationToken));

            if (exists == 0)
            {
                continue;
            }

            // The table name comes from a private readonly array, never from input, so the
            // interpolation here cannot carry anything a caller chose.
            var rows = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {table}", cancellationToken: cancellationToken));

            if (rows > 0)
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                $"DROP TABLE {table}", cancellationToken: cancellationToken));
        }
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
                order_type, limit_price, submitted_utc, status, mode, decision_id)
            VALUES (
                @ClientOrderId, @OptionSymbol, @Side, @Quantity,
                @OrderType, @LimitPrice, @SubmittedUtc, @Status, @Mode, @DecisionId)
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

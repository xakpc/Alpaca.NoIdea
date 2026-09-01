using Dapper;
using Microsoft.Data.Sqlite;
using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Storage;

public sealed record OrderRecord
{
    public string CorrelationId { get; init; } = "";
    public string? ClientOrderId { get; init; }
    public string? AlpacaOrderId { get; init; }
    public string OptionSymbol { get; init; } = "";
    public string Side { get; init; } = "";
    public int Quantity { get; init; }
    public string OrderType { get; init; } = "";
    public decimal? LimitPrice { get; init; }
    public long SubmittedUtc { get; init; }
    public long? ClosedUtc { get; init; }
    public string Status { get; init; } = "";
    public string? RawStatus { get; init; }
    public int FilledQuantity { get; init; }
    public decimal? AverageFillPrice { get; init; }
    public long? ReconciledUtc { get; init; }
    public string Mode { get; init; } = "smoke";
    public long? AuditEventId { get; init; }
}

/// <summary>SQLite persistence for durable decisions, orders, and account history.</summary>
public sealed partial class TradingStore(string connectionString) : IWarRoomAuditSink
{
    public const int CurrentSchemaVersion = 3;

    /// <summary>How long a write waits for another writer before it gives up.</summary>
    public static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(5);

    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    public static string ConnectionStringForFile(string path, bool readOnly = false) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();

    public async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Storage", "Schema.sql"), cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);

        // Write-ahead logging is a property of the database file, so it survives in the file
        // once set and does not need repeating for each connection. It is what lets a reader
        // run while a writer holds the file. The hard-exit loop and the cycle loop both write,
        // and the default rollback journal would make one of them fail rather than wait.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA journal_mode = WAL;", cancellationToken: cancellationToken));

        var version = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version;", cancellationToken: cancellationToken));

        if (version == 0 && await DatabaseHasTablesAsync(connection, cancellationToken))
        {
            throw new InvalidOperationException(
                "The database has an obsolete schema. Start with a clean database file.");
        }

        if (version is not 0 && version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Database schema {version} is not supported; expected {CurrentSchemaVersion}. "
                + "Stop the host, archive trader.db and its SQLite sidecars, then start with a clean file.");
        }

        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {CurrentSchemaVersion};", cancellationToken: cancellationToken));
    }

    private static async Task<bool> DatabaseHasTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
            cancellationToken: cancellationToken)) > 0;

    public async Task ReserveAsync(OrderRecord order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        const string sql =
            """
            INSERT INTO orders (
                audit_event_id, mode, correlation_id, client_order_id, option_symbol,
                side, quantity, order_type, limit_price, submitted_utc, status)
            VALUES (
                @AuditEventId, @Mode, @CorrelationId, @ClientOrderId, @OptionSymbol,
                @Side, @Quantity, @OrderType, @LimitPrice, @SubmittedUtc, @Status)
            """;

        var clientOrderId = string.IsNullOrWhiteSpace(order.ClientOrderId)
            ? throw new ArgumentException("An order needs a client order id.", nameof(order))
            : order.ClientOrderId;
        var correlationId = string.IsNullOrWhiteSpace(order.CorrelationId)
            ? clientOrderId
            : order.CorrelationId;

        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                order.AuditEventId,
                order.Mode,
                CorrelationId = correlationId,
                ClientOrderId = clientOrderId,
                order.OptionSymbol,
                order.Side,
                order.Quantity,
                order.OrderType,
                order.LimitPrice,
                order.SubmittedUtc,
                order.Status,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<long> RecordDecisionAndReserveAsync(
        DecisionEventRow decision,
        OrderRecord order,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(order.ClientOrderId))
        {
            throw new ArgumentException("An order needs a client order id.", nameof(order));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var eventId = await InsertDecisionAsync(connection, transaction, decision, cancellationToken);

        const string sql =
            """
            INSERT INTO orders (
                audit_event_id, mode, correlation_id, client_order_id, option_symbol,
                side, quantity, order_type, limit_price, submitted_utc, status)
            VALUES (
                @eventId, @Mode, @CorrelationId, @ClientOrderId, @OptionSymbol,
                @Side, @Quantity, @OrderType, @LimitPrice, @SubmittedUtc, @Status)
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                eventId,
                order.Mode,
                order.CorrelationId,
                order.ClientOrderId,
                order.OptionSymbol,
                order.Side,
                order.Quantity,
                order.OrderType,
                order.LimitPrice,
                order.SubmittedUtc,
                order.Status,
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return eventId;
    }

    public async Task RecordResultAsync(
        string correlationId,
        string? alpacaOrderId,
        string status,
        long? closedUtc,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE orders
            SET alpaca_order_id = @alpacaOrderId,
                status = @status,
                closed_utc = COALESCE(@closedUtc, closed_utc)
            WHERE correlation_id = @correlationId OR client_order_id = @correlationId
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            sql, new { correlationId, alpacaOrderId, status, closedUtc },
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException(
                $"Order reservation {correlationId} was not found for its broker result.");
        }
    }

    public async Task RecordOrderStateAsync(
        string clientOrderId,
        string? alpacaOrderId,
        string lifecycle,
        string rawStatus,
        int filledQuantity,
        decimal? averageFillPrice,
        long reconciledUtc,
        long? closedUtc,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE orders
            SET alpaca_order_id = COALESCE(@alpacaOrderId, alpaca_order_id),
                status = @lifecycle,
                raw_status = @rawStatus,
                filled_quantity = @filledQuantity,
                average_fill_price = @averageFillPrice,
                reconciled_utc = @reconciledUtc,
                closed_utc = COALESCE(@closedUtc, closed_utc)
            WHERE client_order_id = @clientOrderId
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                clientOrderId,
                alpacaOrderId,
                lifecycle,
                rawStatus,
                filledQuantity,
                averageFillPrice,
                reconciledUtc,
                closedUtc,
            },
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException(
                $"Order reservation {clientOrderId} was not found for reconciliation.");
        }
    }

    public async Task<OrderRecord?> FindAsync(
        string clientOrderId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT correlation_id AS CorrelationId, client_order_id AS ClientOrderId,
                   alpaca_order_id AS AlpacaOrderId, option_symbol AS OptionSymbol,
                   side AS Side, quantity AS Quantity, order_type AS OrderType,
                   limit_price AS LimitPrice, submitted_utc AS SubmittedUtc,
                   closed_utc AS ClosedUtc, status AS Status, raw_status AS RawStatus,
                   filled_quantity AS FilledQuantity, average_fill_price AS AverageFillPrice,
                   reconciled_utc AS ReconciledUtc, mode AS Mode,
                   audit_event_id AS AuditEventId
            FROM orders WHERE client_order_id = @clientOrderId
            """;
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OrderRecord>(new CommandDefinition(
            sql, new { clientOrderId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Opens one connection for one operation.
    /// </summary>
    /// <remarks>
    /// The store opens a new connection for each operation rather than holding one, so the
    /// busy timeout must be set here: a pragma applies to the connection that runs it, not to
    /// the database file. Without it the timeout is zero and the second of two concurrent
    /// writers fails immediately with <c>SQLITE_BUSY</c>. The hard-exit loop and the cycle
    /// loop both write, so this is a correctness requirement and not a performance option.
    /// Five seconds is far longer than any write this application makes.
    /// </remarks>
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA busy_timeout = {(int)BusyTimeout.TotalMilliseconds};",
            cancellationToken: cancellationToken));
        return connection;
    }
}

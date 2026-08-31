using System.Text.Json;
using Dapper;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Storage;

public sealed record PositionReviewState(
    string OptionSymbol,
    DateTimeOffset LastReviewedUtc,
    long LastNewsSeen);

public sealed partial class TradingStore
{
    public async Task<IReadOnlyList<OrderRecord>> UnsettledOrdersAsync(
        string mode, CancellationToken cancellationToken)
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
            FROM orders
            WHERE mode = @mode
              AND status COLLATE NOCASE IN ('Reserved', 'Uncertain', 'Open', 'PartiallyFilled')
            ORDER BY submitted_utc, id
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<OrderRecord>(new CommandDefinition(
            sql, new { mode }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<StrategyPolicy?> LoadPolicyAsync(
        string mode, CancellationToken cancellationToken)
    {
        const string sql = "SELECT policy_json FROM strategy_state WHERE mode = @mode";
        await using var connection = await OpenAsync(cancellationToken);
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            sql, new { mode }, cancellationToken: cancellationToken));
        return json is null ? null : JsonSerializer.Deserialize<StrategyPolicy>(json);
    }

    public async Task SavePolicyAsync(
        string mode,
        StrategyPolicy policy,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO strategy_state (mode, policy_json, updated_utc)
            VALUES (@mode, @policyJson, @updatedUtc)
            ON CONFLICT(mode) DO UPDATE SET
                policy_json = excluded.policy_json,
                updated_utc = excluded.updated_utc
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                mode,
                policyJson = JsonSerializer.Serialize(policy),
                updatedUtc = updatedUtc.ToUnixTimeSeconds(),
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PositionReviewState>> LoadPositionReviewStateAsync(
        string mode, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT option_symbol AS OptionSymbol,
                   last_reviewed_utc AS LastReviewedUtcSeconds,
                   last_news_seen AS LastNewsSeen
            FROM position_review_state
            WHERE mode = @mode
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PositionReviewStateRow>(new CommandDefinition(
            sql, new { mode }, cancellationToken: cancellationToken));
        return rows.Select(row => new PositionReviewState(
            row.OptionSymbol,
            DateTimeOffset.FromUnixTimeSeconds(row.LastReviewedUtcSeconds),
            row.LastNewsSeen)).ToArray();
    }

    public async Task SavePositionReviewStateAsync(
        string mode,
        string optionSymbol,
        DateTimeOffset reviewedUtc,
        long lastNewsSeen,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO position_review_state (
                mode, option_symbol, last_reviewed_utc, last_news_seen)
            VALUES (@mode, @optionSymbol, @reviewedUtc, @lastNewsSeen)
            ON CONFLICT(mode, option_symbol) DO UPDATE SET
                last_reviewed_utc = excluded.last_reviewed_utc,
                last_news_seen = excluded.last_news_seen
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                mode,
                optionSymbol,
                reviewedUtc = reviewedUtc.ToUnixTimeSeconds(),
                lastNewsSeen,
            },
            cancellationToken: cancellationToken));
    }

    private sealed record PositionReviewStateRow
    {
        public string OptionSymbol { get; init; } = "";
        public long LastReviewedUtcSeconds { get; init; }
        public long LastNewsSeen { get; init; }
    }
}

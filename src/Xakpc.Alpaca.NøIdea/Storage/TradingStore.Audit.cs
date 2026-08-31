using Dapper;

namespace Xakpc.Alpaca.NøIdea.Storage;

/// <summary>One evaluated option event: the room sat over this contract at this price.</summary>
public sealed record EvaluationRunRow
{
    public long TimestampUtc { get; init; }
    public string Mode { get; init; } = "live";
    public string? ProposalId { get; init; }
    public string Symbol { get; init; } = "";
    public decimal CurrentPrice { get; init; }
    public string OptionSymbol { get; init; } = "";
    public string OptionType { get; init; } = "";
    public decimal Strike { get; init; }
    public long ExpirationUtc { get; init; }
    public decimal? MarketProbability { get; init; }
    public string Status { get; init; } = "";
    public string? MarketSnapshotJson { get; init; }
}

/// <summary>One seat's opinion of one evaluated contract.</summary>
public sealed record ForecastRow
{
    public long RunId { get; init; }
    public string Forecaster { get; init; } = "";
    public string? Vote { get; init; }
    public decimal? Probability { get; init; }
    public decimal? Confidence { get; init; }
    public string? Reasoning { get; init; }
    public string? EvidenceJson { get; init; }
    public long CreatedUtc { get; init; }
}

/// <summary>What the system decided, and what the guardrails said about it.</summary>
public sealed record DecisionRow
{
    public long RunId { get; init; }
    public decimal? CombinedProbability { get; init; }
    public decimal? MarketProbability { get; init; }
    public decimal? Edge { get; init; }
    public decimal? NetVote { get; init; }
    public string Action { get; init; } = "";
    public string? Reason { get; init; }
    public string? RiskResult { get; init; }
    public long CreatedUtc { get; init; }
}

/// <summary>
/// The audit-trail writes. What the agent thought, and what the guardrails allowed.
/// </summary>
/// <remarks>
/// <para>
/// These answer the questions in <c>.lode/operations/observability.md</c>: what each seat
/// said, what the option market showed at the time, why the system traded, which rule
/// stopped it, and which order Alpaca received. The log tells the same story in prose; this
/// is the half that can be queried.
/// </para>
/// <para>
/// <b>A rejected action is recorded exactly like an accepted one.</b> A run that stores only
/// its trades cannot demonstrate that a risk rule ever fired.
/// </para>
/// </remarks>
public sealed partial class TradingStore
{
    /// <summary>Records one evaluated contract and returns its id.</summary>
    public async Task<long> RecordEvaluationAsync(
        EvaluationRunRow run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        const string sql =
            """
            INSERT INTO evaluation_runs (
                timestamp_utc, mode, proposal_id, symbol, current_price, option_symbol,
                option_type, strike, expiration_utc, market_probability, status,
                market_snapshot_json)
            VALUES (
                @TimestampUtc, @Mode, @ProposalId, @Symbol, @CurrentPrice, @OptionSymbol,
                @OptionType, @Strike, @ExpirationUtc, @MarketProbability, @Status,
                @MarketSnapshotJson);
            SELECT last_insert_rowid();
            """;

        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, run, cancellationToken: cancellationToken));
    }

    /// <summary>Records every seat's opinion of one evaluated contract.</summary>
    public async Task RecordForecastsAsync(
        IReadOnlyCollection<ForecastRow> forecasts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forecasts);

        if (forecasts.Count == 0)
        {
            return;
        }

        const string sql =
            """
            INSERT INTO forecasts (
                run_id, forecaster, vote, probability, confidence, reasoning,
                evidence_json, created_utc)
            VALUES (
                @RunId, @Forecaster, @Vote, @Probability, @Confidence, @Reasoning,
                @EvidenceJson, @CreatedUtc)
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            sql, forecasts, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records one decision and returns its id, for the order row to point at.</summary>
    public async Task<long> RecordDecisionAsync(
        DecisionRow decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);

        const string sql =
            """
            INSERT INTO decisions (
                run_id, combined_probability, market_probability, edge, net_vote,
                action, reason, risk_result, created_utc)
            VALUES (
                @RunId, @CombinedProbability, @MarketProbability, @Edge, @NetVote,
                @Action, @Reason, @RiskResult, @CreatedUtc);
            SELECT last_insert_rowid();
            """;

        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, decision, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The most recent decisions, newest first, joined to their evaluation and order.
    /// </summary>
    /// <remarks>
    /// A LEFT JOIN on orders, deliberately: a rejected decision has no order, and it is
    /// precisely those rows that show the guardrails doing their job.
    /// </remarks>
    public async Task<IReadOnlyList<AuditEntry>> RecentDecisionsAsync(
        int limit, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT r.timestamp_utc         AS TimestampUtc,
                   r.mode                  AS Mode,
                   r.status                AS Status,
                   d.action                AS Action,
                   r.option_symbol         AS OptionSymbol,
                   d.combined_probability  AS CombinedProbability,
                   d.market_probability    AS MarketProbability,
                   d.net_vote              AS NetVote,
                   d.risk_result           AS RiskResult,
                   (SELECT COUNT(*) FROM forecasts f WHERE f.run_id = r.id) AS SeatCount,
                   o.client_order_id       AS ClientOrderId
            FROM decisions d
            JOIN evaluation_runs r ON r.id = d.run_id
            LEFT JOIN orders o ON o.decision_id = d.id
            ORDER BY d.id DESC
            LIMIT @limit
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditEntry>(
            new CommandDefinition(sql, new { limit }, cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <summary>Row counts for the audit tables, for the tests and the operator.</summary>
    public async Task<IReadOnlyDictionary<string, long>> AuditRowCountsAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT 'evaluation_runs' AS TableName, COUNT(*) AS Rows FROM evaluation_runs
            UNION ALL SELECT 'forecasts', COUNT(*) FROM forecasts
            UNION ALL SELECT 'decisions', COUNT(*) FROM decisions
            UNION ALL SELECT 'orders', COUNT(*) FROM orders
            UNION ALL SELECT 'equity_snapshots', COUNT(*) FROM equity_snapshots
            """;

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string TableName, long Rows)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));

        return rows.ToDictionary(row => row.TableName, row => row.Rows, StringComparer.Ordinal);
    }
}

/// <summary>One decision, joined to its evaluation and its order, for reading back.</summary>
public sealed record AuditEntry
{
    public long TimestampUtc { get; init; }
    public string Mode { get; init; } = "";
    public string Status { get; init; } = "";
    public string Action { get; init; } = "";
    public string OptionSymbol { get; init; } = "";
    public decimal? CombinedProbability { get; init; }
    public decimal? MarketProbability { get; init; }
    public decimal? NetVote { get; init; }
    public string? RiskResult { get; init; }
    public long SeatCount { get; init; }
    public string? ClientOrderId { get; init; }
}

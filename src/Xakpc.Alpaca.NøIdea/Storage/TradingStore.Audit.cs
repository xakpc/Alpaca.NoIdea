using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Storage;

public sealed record DecisionEventRow
{
    public long TimestampUtc { get; init; }
    public string Mode { get; init; } = "live";
    public string? ProposalId { get; init; }
    public string Purpose { get; init; } = "new-trade";
    public string Action { get; init; } = "Hold";
    public string Outcome { get; init; } = "held";
    public string? Reason { get; init; }
    public string? RiskResult { get; init; }
    public string? Symbol { get; init; }
    public string? OptionSymbol { get; init; }
    public string? OptionType { get; init; }
    public decimal? Strike { get; init; }
    public long? ExpirationUtc { get; init; }
    public decimal? UnderlyingPrice { get; init; }
    public decimal? Probability { get; init; }
    public decimal? NetVote { get; init; }
    public string? MarketSnapshotJson { get; init; }
}

public sealed record PositionThesis(
    string ContractSymbol,
    string Thesis,
    IReadOnlyList<string> Conditions);

public sealed partial class TradingStore
{
    public async Task BeginSittingAsync(
        string proposalId,
        string mode,
        WarRoomPurpose purpose,
        long startedUtc,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO war_room_sittings (
                proposal_id, mode, purpose, started_utc, status)
            VALUES (@proposalId, @mode, @purpose, @startedUtc, 'running')
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { proposalId, mode, purpose = purpose.ToString(), startedUtc },
            cancellationToken: cancellationToken));
    }

    public async Task RecordToolCallsAsync(
        string proposalId,
        IReadOnlyCollection<AgentToolCallAudit> calls,
        CancellationToken cancellationToken)
    {
        if (calls.Count == 0)
        {
            return;
        }

        const string sql =
            """
            INSERT INTO agent_tool_calls (
                proposal_id, persona, phase, model, call_id, tool_name,
                arguments_json, result_json, status, captured_utc)
            VALUES (
                @ProposalId, @Persona, @Phase, @Model, @CallId, @ToolName,
                @ArgumentsJson, @ResultJson, @Status, unixepoch())
            """;
        var rows = calls.Select(call => new
        {
            ProposalId = proposalId,
            call.Persona,
            call.Phase,
            call.Model,
            call.CallId,
            call.ToolName,
            call.ArgumentsJson,
            call.ResultJson,
            call.Status,
        });

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql, rows, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteSittingAsync(
        string proposalId,
        WarRoomVerdict verdict,
        IReadOnlyCollection<ProposalReviewPass> passes,
        long completedUtc,
        CancellationToken cancellationToken)
    {
        const string insert =
            """
            INSERT INTO proposal_review_passes (
                proposal_id, proposal_version, review_pass, superseded, verdict,
                rejection_code, option_symbol, thesis, thesis_conditions_json,
                operation_json, analyses_json, discussion_json, votes_json, created_utc)
            VALUES (
                @ProposalId, @ProposalVersion, @ReviewPass, @Superseded, @Verdict,
                @RejectionCode, @OptionSymbol, @Thesis, @ThesisConditionsJson,
                @OperationJson, @AnalysesJson, @DiscussionJson, @VotesJson, @CreatedUtc)
            """;
        const string complete =
            """
            UPDATE war_room_sittings
            SET completed_utc = @completedUtc, verdict = @verdict, status = 'completed'
            WHERE proposal_id = @proposalId
            """;

        var rows = passes.Select(pass => new
        {
            pass.ProposalId,
            pass.ProposalVersion,
            pass.ReviewPass,
            pass.Superseded,
            Verdict = pass.Verdict.ToString(),
            pass.RejectionCode,
            OptionSymbol = pass.Operation.Actions
                .FirstOrDefault(action => action.Kind != Agents.StrategyActionKind.Hold)
                ?.ContractSymbol,
            pass.Operation.Thesis,
            ThesisConditionsJson = JsonSerializer.Serialize(pass.Operation.ThesisConditions),
            OperationJson = JsonSerializer.Serialize(pass.Operation),
            AnalysesJson = JsonSerializer.Serialize(pass.Analyses),
            DiscussionJson = JsonSerializer.Serialize(pass.Discussion),
            VotesJson = JsonSerializer.Serialize(pass.Votes),
            CreatedUtc = completedUtc,
        }).ToArray();

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (rows.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insert, rows, transaction, cancellationToken: cancellationToken));
        }
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            complete,
            new { proposalId, verdict = verdict.ToString(), completedUtc },
            transaction,
            cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException($"Sitting {proposalId} was not started.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Gives every interrupted sitting a terminal status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sitting is opened with status <c>running</c> and is completed at the end. A process
    /// that stops between the two leaves the row as it was, and <c>--audit</c> then reports
    /// <c>incomplete_sitting</c> forever. The record is not corrupt; it is unfinished, and
    /// nothing could say so.
    /// </para>
    /// <para>
    /// The row is updated and never deleted. <c>decision_events</c> and <c>agent_tool_calls</c>
    /// carry a foreign key to <c>proposal_id</c>, and an interrupted sitting is evidence: an
    /// audit that proves the record complete by erasing the inconvenient row proves nothing.
    /// The <c>abandoned</c> status is terminal, so the two status-scoped checks that look for a
    /// missing review pass and a missing decision correctly ignore it.
    /// </para>
    /// </remarks>
    /// <returns>How many sittings were marked.</returns>
    public async Task<int> RecoverInterruptedSittingsAsync(
        long completedUtc,
        string fault,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE war_room_sittings
            SET status = 'abandoned', completed_utc = @completedUtc, fault = @fault
            WHERE status = 'running'
            """;

        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            sql, new { completedUtc, fault }, cancellationToken: cancellationToken));
    }

    /// <summary>The sittings that a recovery would mark.</summary>
    public async Task<IReadOnlyList<string>> InterruptedSittingsAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT proposal_id FROM war_room_sittings WHERE status = 'running' ORDER BY started_utc";

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return [.. rows];
    }

    public async Task<long> RecordDecisionEventAsync(
        DecisionEventRow decision,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await InsertDecisionAsync(connection, null, decision, cancellationToken);
    }

    internal static async Task<long> InsertDecisionAsync(
        SqliteConnection connection,
        DbTransaction? transaction,
        DecisionEventRow decision,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO decision_events (
                timestamp_utc, mode, proposal_id, purpose, action, outcome,
                reason, risk_result, symbol, option_symbol, option_type, strike,
                expiration_utc, underlying_price, probability, net_vote,
                market_snapshot_json)
            VALUES (
                @TimestampUtc, @Mode, @ProposalId, @Purpose, @Action, @Outcome,
                @Reason, @RiskResult, @Symbol, @OptionSymbol, @OptionType, @Strike,
                @ExpirationUtc, @UnderlyingPrice, @Probability, @NetVote,
                @MarketSnapshotJson);
            SELECT last_insert_rowid();
            """;
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql, decision, transaction, cancellationToken: cancellationToken));
    }

    public async Task UpdateDecisionOutcomeAsync(
        long eventId,
        string outcome,
        string? riskResult,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE decision_events
            SET outcome = @outcome, risk_result = COALESCE(@riskResult, risk_result)
            WHERE id = @eventId
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            sql, new { eventId, outcome, riskResult }, cancellationToken: cancellationToken));
        if (changed != 1)
        {
            throw new InvalidOperationException($"Decision event {eventId} was not found.");
        }
    }

    public async Task<IReadOnlyDictionary<string, PositionThesis>> PositionThesesAsync(
        string mode,
        IReadOnlyCollection<string> contractSymbols,
        CancellationToken cancellationToken)
    {
        if (contractSymbols.Count == 0)
        {
            return new Dictionary<string, PositionThesis>(StringComparer.Ordinal);
        }

        const string sql =
            """
            SELECT o.option_symbol AS ContractSymbol, p.thesis AS Thesis,
                   p.thesis_conditions_json AS ConditionsJson
            FROM orders o
            JOIN decision_events e ON e.id = o.audit_event_id
            JOIN proposal_review_passes p ON p.proposal_id = e.proposal_id
            WHERE o.mode = @mode AND o.option_symbol IN @contractSymbols
              AND p.superseded = 0
            ORDER BY p.id DESC
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PositionThesisDbRow>(new CommandDefinition(
            sql, new { mode, contractSymbols }, cancellationToken: cancellationToken));
        return rows.GroupBy(row => row.ContractSymbol, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new PositionThesis(
                    group.Key,
                    group.First().Thesis,
                    JsonSerializer.Deserialize<string[]>(group.First().ConditionsJson) ?? []),
                StringComparer.Ordinal);
    }

    public async Task RecordEquityAsync(
        long timestampUtc,
        string mode,
        decimal equity,
        decimal cash,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT OR REPLACE INTO equity_snapshots (timestamp_utc, mode, equity, cash)
            VALUES (@timestampUtc, @mode, @equity, @cash)
            """;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { timestampUtc, mode, equity, cash }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AuditEntry>> RecentDecisionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT e.timestamp_utc AS TimestampUtc, e.mode AS Mode,
                   e.purpose AS Purpose, e.outcome AS Outcome, e.action AS Action,
                   e.option_symbol AS OptionSymbol, e.reason AS Reason,
                   e.risk_result AS RiskResult, e.proposal_id AS ProposalId,
                   (SELECT COUNT(*) FROM agent_tool_calls t
                    WHERE t.proposal_id = e.proposal_id) AS ToolCallCount,
                   o.correlation_id AS CorrelationId, o.status AS OrderStatus
            FROM decision_events e
            LEFT JOIN orders o ON o.audit_event_id = e.id
            ORDER BY e.id DESC LIMIT @limit
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditEntry>(new CommandDefinition(
            sql, new { limit }, cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>
    /// The new-trade operations the room most recently refused, newest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Feeds the room its own short-term memory. A sitting starts from nothing, so without
    /// this the proposer re-derives an already-defeated thesis and pays for the same debate
    /// again. Held and rejected both count: a hold is a refusal to open, whatever produced it.
    /// </para>
    /// <para>
    /// <b>Every mode is read, not only the caller's.</b> A refusal records a judgement about
    /// the market, and the market does not know which mode observed it. Scoping this by mode
    /// empties the memory in the case that needs it most: a dry run rehearsing what the live
    /// loop already refused an hour earlier. <see cref="RecentDecisionsAsync"/> is
    /// mode-agnostic for the same reason.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RecentRejection>> RecentRejectionsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT timestamp_utc AS TimestampUtc,
                   option_symbol AS OptionSymbol,
                   reason AS Reason
            FROM decision_events
            WHERE purpose = 'new-trade'
              AND outcome IN ('rejected', 'held')
            ORDER BY id DESC LIMIT @limit
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<RejectionRow>(new CommandDefinition(
            sql, new { limit }, cancellationToken: cancellationToken));

        return [.. rows.Select(row => new RecentRejection(
            DateTimeOffset.FromUnixTimeSeconds(row.TimestampUtc),
            row.OptionSymbol,
            row.Reason))];
    }

    /// <summary>Dapper's shape for <see cref="RecentRejectionsAsync"/>: the row stores unix seconds.</summary>
    private sealed record RejectionRow
    {
        public long TimestampUtc { get; init; }
        public string? OptionSymbol { get; init; }
        public string? Reason { get; init; }
    }

    public async Task<IReadOnlyDictionary<string, long>> AuditRowCountsAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT 'war_room_sittings' AS TableName, COUNT(*) AS Rows FROM war_room_sittings
            UNION ALL SELECT 'proposal_review_passes', COUNT(*) FROM proposal_review_passes
            UNION ALL SELECT 'agent_tool_calls', COUNT(*) FROM agent_tool_calls
            UNION ALL SELECT 'decision_events', COUNT(*) FROM decision_events
            UNION ALL SELECT 'orders', COUNT(*) FROM orders
            UNION ALL SELECT 'equity_snapshots', COUNT(*) FROM equity_snapshots
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string TableName, long Rows)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.ToDictionary(row => row.TableName, row => row.Rows, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<AuditIntegrityIssue>> AuditIntegrityAsync(
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT 'incomplete_sitting' AS Code, proposal_id AS Reference
            FROM war_room_sittings
            WHERE status = 'running'
            UNION ALL
            SELECT 'missing_review_pass', proposal_id
            FROM war_room_sittings s
            WHERE status = 'completed'
              AND NOT EXISTS (
                  SELECT 1 FROM proposal_review_passes p
                  WHERE p.proposal_id = s.proposal_id)
            UNION ALL
            SELECT 'missing_sitting_decision', proposal_id
            FROM war_room_sittings s
            WHERE status = 'completed'
              AND NOT EXISTS (
                  SELECT 1 FROM decision_events e
                  WHERE e.proposal_id = s.proposal_id)
            UNION ALL
            SELECT 'incomplete_tool_call',
                   proposal_id || ':' || persona || ':' || phase || ':' || call_id
            FROM agent_tool_calls
            WHERE status <> 'completed' OR result_json IS NULL
            UNION ALL
            SELECT 'unlinked_autonomous_order', correlation_id
            FROM orders
            WHERE mode IN ('live', 'dry-run') AND audit_event_id IS NULL
            UNION ALL
            SELECT 'missing_order_decision', correlation_id
            FROM orders o
            WHERE audit_event_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM decision_events e WHERE e.id = o.audit_event_id)
            UNION ALL
            SELECT 'foreign_key_fault',
                   "table" || ':' || rowid || ':' || parent
            FROM pragma_foreign_key_check
            """;
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<AuditIntegrityIssue>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return [.. rows];
    }

    public async Task<long> SchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version;", cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The journal mode the database file is in. Expected to be <c>wal</c>.
    /// </summary>
    /// <remarks>
    /// The cycle loop and the hard-exit loop both write, and the default rollback journal makes
    /// one of two concurrent writers fail rather than wait.
    /// </remarks>
    public async Task<string> JournalModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "PRAGMA journal_mode;", cancellationToken: cancellationToken)) ?? "";
    }

    private sealed record PositionThesisDbRow
    {
        public string ContractSymbol { get; init; } = "";
        public string Thesis { get; init; } = "";
        public string ConditionsJson { get; init; } = "[]";
    }
}

public sealed record AuditEntry
{
    public long TimestampUtc { get; init; }
    public string Mode { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string Action { get; init; } = "";
    public string? OptionSymbol { get; init; }
    public string? Reason { get; init; }
    public string? RiskResult { get; init; }
    public string? ProposalId { get; init; }
    public long ToolCallCount { get; init; }
    public string? CorrelationId { get; init; }
    public string? OrderStatus { get; init; }
}

public sealed record AuditIntegrityIssue
{
    public string Code { get; init; } = "";
    public string Reference { get; init; } = "";
}

using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Trading;

public sealed record OrderSubmissionResult(OrderState Order, bool BrokerConfirmed);

/// <summary>Owns durable reservation, broker submission, recovery, and lifecycle updates.</summary>
public sealed class OrderCoordinator(
    ITradingGateway trading,
    TradingStore store,
    TimeProvider time,
    ILogger logger,
    string mode)
{
    private readonly ITradingGateway _trading = trading ?? throw new ArgumentNullException(nameof(trading));
    private readonly TradingStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly string _mode = mode ?? throw new ArgumentNullException(nameof(mode));

    public async Task<IReadOnlyList<OrderState>> ReconcileAndListPendingAsync(
        bool replayMissingSells,
        CancellationToken cancellationToken)
    {
        var local = await StoreAsync(
            () => _store.UnsettledOrdersAsync(_mode, cancellationToken),
            "Could not read unsettled order reservations.");
        var unresolved = new List<OrderState>();

        foreach (var record in local)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var broker = await TryFindAsync(record.ClientOrderId!, cancellationToken);
            var replayedSell = false;

            if (broker is null && replayMissingSells
                && string.Equals(record.Side, "Sell", StringComparison.OrdinalIgnoreCase))
            {
                replayedSell = true;
                broker = await RetryRiskReducingSellAsync(record, cancellationToken);
            }

            if (broker is not null)
            {
                await PersistStateAsync(record, broker, cancellationToken);
                if (!broker.IsTerminal)
                {
                    unresolved.Add(broker);
                }
                else if (replayedSell && broker.Lifecycle != OrderLifecycle.Filled)
                {
                    // A duplicate-client-id rejection can race broker lookup visibility.
                    // Quarantine this symbol for the rest of the cycle before a fresh id is allowed.
                    unresolved.Add(broker with
                    {
                        Lifecycle = OrderLifecycle.Uncertain,
                        RawStatus = $"replay terminal; recheck next cycle: {broker.RawStatus}",
                    });
                }
            }
            else
            {
                unresolved.Add(ToOrderState(record));
            }
        }

        var brokerOpen = await _trading.ListOpenOrdersAsync(cancellationToken);
        return brokerOpen
            .Concat(unresolved)
            .GroupBy(order => string.IsNullOrWhiteSpace(order.ClientOrderId)
                    ? $"broker:{order.BrokerOrderId}"
                    : $"client:{order.ClientOrderId}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<OrderSubmissionResult> SubmitAsync(
        DecisionEventRow decision,
        OrderRequest request,
        bool riskReducing,
        CancellationToken cancellationToken)
    {
        AuditPersistenceException? auditFailure = null;
        long? eventId = null;
        try
        {
            eventId = await _store.RecordDecisionAndReserveAsync(
                decision,
                new OrderRecord
                {
                    CorrelationId = request.ClientOrderId,
                    ClientOrderId = request.ClientOrderId,
                    OptionSymbol = request.ContractSymbol,
                    Side = request.IsBuy ? "Buy" : "Sell",
                    Quantity = request.Quantity,
                    OrderType = request.LimitPrice is null ? "Market" : "Limit",
                    LimitPrice = request.LimitPrice,
                    SubmittedUtc = _time.GetUtcNow().ToUnixTimeSeconds(),
                    Status = OrderLifecycle.Reserved.ToString(),
                    Mode = _mode,
                },
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            auditFailure = error as AuditPersistenceException
                ?? new AuditPersistenceException(
                    $"Could not reserve the order for {request.ContractSymbol}.", error);
            if (!riskReducing)
            {
                throw auditFailure;
            }
        }

        OrderState? broker = null;
        Exception? submitFailure = null;
        try
        {
            broker = await _trading.SubmitOrderAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            submitFailure = error;
            broker = await TryFindAsync(request.ClientOrderId, cancellationToken);
        }

        if (auditFailure is not null)
        {
            if (submitFailure is not null)
            {
                _logger.LogError(
                    submitFailure,
                    "The risk-reducing submit for {Symbol} also failed after the audit failure.",
                    request.ContractSymbol);
            }

            throw auditFailure;
        }

        if (broker is null)
        {
            var uncertain = new OrderState
            {
                ClientOrderId = request.ClientOrderId,
                ContractSymbol = request.ContractSymbol,
                Lifecycle = OrderLifecycle.Uncertain,
                FilledQuantity = 0,
                RequestedQuantity = request.Quantity,
                IsBuy = request.IsBuy,
                LimitPrice = request.LimitPrice,
                SubmittedUtc = _time.GetUtcNow(),
                RawStatus = submitFailure is null ? "broker order not found" : $"submit uncertain: {submitFailure.Message}",
            };
            await PersistStateAsync(
                new OrderRecord
                {
                    ClientOrderId = request.ClientOrderId,
                    AuditEventId = eventId,
                    Side = request.IsBuy ? "Buy" : "Sell",
                },
                uncertain,
                cancellationToken);
            return new OrderSubmissionResult(uncertain, false);
        }

        await PersistStateAsync(
            new OrderRecord
            {
                ClientOrderId = request.ClientOrderId,
                AuditEventId = eventId,
                Side = request.IsBuy ? "Buy" : "Sell",
            },
            broker,
            cancellationToken);
        return new OrderSubmissionResult(broker, true);
    }

    private async Task<OrderState?> RetryRiskReducingSellAsync(
        OrderRecord record, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning(
                "Retrying unresolved close {ClientOrderId} for {Symbol} with the same client id.",
                record.ClientOrderId, record.OptionSymbol);
            return await _trading.SubmitOrderAsync(
                new OrderRequest
                {
                    ClientOrderId = record.ClientOrderId!,
                    ContractSymbol = record.OptionSymbol,
                    Quantity = record.Quantity,
                    IsBuy = false,
                    LimitPrice = null,
                },
                cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogError(
                error,
                "The unresolved close {ClientOrderId} is still uncertain.",
                record.ClientOrderId);
            return await TryFindAsync(record.ClientOrderId!, cancellationToken);
        }
    }

    private async Task<OrderState?> TryFindAsync(
        string clientOrderId, CancellationToken cancellationToken)
    {
        try
        {
            return await _trading.FindOrderByClientIdAsync(clientOrderId, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning(
                error,
                "Could not reconcile client order {ClientOrderId}; it remains unresolved.",
                clientOrderId);
            return null;
        }
    }

    private async Task PersistStateAsync(
        OrderRecord record, OrderState broker, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        try
        {
            await _store.RecordOrderStateAsync(
                record.ClientOrderId!,
                broker.BrokerOrderId,
                broker.Lifecycle.ToString(),
                broker.RawStatus,
                broker.FilledQuantity,
                broker.AverageFillPrice,
                now.ToUnixTimeSeconds(),
                broker.IsTerminal ? now.ToUnixTimeSeconds() : null,
                cancellationToken);

            if (record.AuditEventId is { } eventId)
            {
                await _store.UpdateDecisionOutcomeAsync(
                    eventId,
                    DecisionOutcome(record.Side, broker.Lifecycle),
                    broker.RawStatus,
                    cancellationToken);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException(
                $"Could not store the broker lifecycle for {record.ClientOrderId}.", error);
        }
    }

    private static string DecisionOutcome(string side, OrderLifecycle lifecycle) =>
        string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase)
            ? lifecycle switch
            {
                OrderLifecycle.Filled => "closed",
                OrderLifecycle.PartiallyFilled => "partially-closed",
                OrderLifecycle.Open => "close-pending",
                OrderLifecycle.Uncertain or OrderLifecycle.Reserved => "close-uncertain",
                _ => lifecycle.ToString().ToLowerInvariant(),
            }
            : lifecycle switch
            {
                OrderLifecycle.PartiallyFilled => "partially-filled",
                OrderLifecycle.Uncertain or OrderLifecycle.Reserved => "uncertain",
                _ => lifecycle.ToString().ToLowerInvariant(),
            };

    private static OrderState ToOrderState(OrderRecord record) => new()
    {
        ClientOrderId = record.ClientOrderId!,
        BrokerOrderId = record.AlpacaOrderId,
        ContractSymbol = record.OptionSymbol,
        Lifecycle = Enum.TryParse<OrderLifecycle>(record.Status, out var lifecycle)
            ? lifecycle
            : OrderLifecycle.Uncertain,
        FilledQuantity = record.FilledQuantity,
        RequestedQuantity = record.Quantity,
        IsBuy = string.Equals(record.Side, "Buy", StringComparison.OrdinalIgnoreCase),
        LimitPrice = record.LimitPrice,
        SubmittedUtc = DateTimeOffset.FromUnixTimeSeconds(record.SubmittedUtc),
        AverageFillPrice = record.AverageFillPrice,
        RawStatus = record.RawStatus ?? record.Status,
    };

    private static async Task<T> StoreAsync<T>(Func<Task<T>> action, string message)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (error is not OperationCanceledException
                                      and not AuditPersistenceException)
        {
            throw new AuditPersistenceException(message, error);
        }
    }
}

using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Observability;
using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>
/// Runs the trading loop against the live paper account until it is stopped.
/// </summary>
/// <remarks>
/// <para>
/// The session owns pacing and restart behaviour; the cycle itself is
/// <see cref="TradingLoop"/>.
/// </para>
/// <para>
/// A transient market or broker fault does not stop the session. An audit persistence fault
/// does stop it because a later trade without a durable record is not permitted.
/// </para>
/// </remarks>
public sealed class LiveSession(
    TradingLoop loop,
    IMarketDataGateway marketData,
    TradingOptions options,
    RiskOptions riskOptions,
    TimeProvider time,
    ILogger logger)
{
    private readonly TradingLoop _loop = loop ?? throw new ArgumentNullException(nameof(loop));
    private readonly IMarketDataGateway _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
    private readonly TradingOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly RiskOptions _riskOptions = riskOptions ?? throw new ArgumentNullException(nameof(riskOptions));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// What the room has spent so far. The session does not own the agent, so it asks.
    /// </summary>
    /// <remarks>
    /// The default reports nothing, which is correct for a stub agent that spends nothing.
    /// The live host supplies <c>WarRoomAgent.TotalCost</c>.
    /// </remarks>
    public Func<Agents.Room.RoomCost> RunningCost { get; init; } = static () => new();

    /// <summary>Run one cycle and stop, even when the exchange is shut.</summary>
    /// <remarks>
    /// The only way to exercise the live path out of hours. Pair it with a dry run: quotes
    /// are stale and an order left resting on a closed session could fill at the next open.
    /// </remarks>
    public bool RunOnceIgnoringMarketHours { get; init; }

    /// <summary>How many cycles actually ran.</summary>
    public int CyclesRun { get; private set; }

    /// <summary>How many deterministic exit passes ran.</summary>
    public int HardExitPassesRun { get; private set; }

    /// <summary>An audit fault the exit loop saw, rethrown once both loops have stopped.</summary>
    private AuditPersistenceException? _exitLoopFault;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Live session starting. {Symbols} symbols, {Interval} minute cycle, "
            + "{ExitInterval} minute exit check, flatten at {Flatten:u}.",
            _options.TrackedSymbols.Count, _options.CycleInterval.TotalMinutes,
            _options.HardExitInterval.TotalMinutes, _riskOptions.CompetitionFlattenUtc);

        // The exits are C# rules that consult no model, so they get their own timer. At the
        // cycle cadence a stop-loss is only sampled every 38 to 41 minutes, which on a one to
        // three day option is not a stop at all. A single cycle is a diagnostic and starts no
        // second loop.
        using var sessionStopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitLoop = RunOnceIgnoringMarketHours
            ? Task.CompletedTask
            : RunHardExitLoopAsync(sessionStopping);

        try
        {
            await RunCycleLoopAsync(sessionStopping.Token);
        }
        finally
        {
            // Stop the exit loop before returning, so a stopped session leaves nothing behind
            // that can still reach the broker.
            await sessionStopping.CancelAsync();
            await exitLoop;
        }

        // An audit fault in the exit loop stops the session the same way one in a cycle does.
        // It is rethrown here because the loop that saw it cannot throw into this call.
        if (_exitLoopFault is { } fault)
        {
            throw fault;
        }
    }

    /// <summary>
    /// Checks the deterministic exits on their own short timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pass costs two Alpaca reads and no model tokens, so it is paced by how fast a short
    /// dated option moves rather than by cost. Alpaca has no stop order type and no bracket
    /// order class for options, so there is no broker-side alternative to this loop.
    /// </para>
    /// <para>
    /// A transient fault is logged and the loop continues, exactly as a cycle fault does. An
    /// audit persistence fault stops the whole session, so it cancels the cycle loop rather
    /// than disappearing into a task nobody awaits until shutdown.
    /// </para>
    /// </remarks>
    private async Task RunHardExitLoopAsync(CancellationTokenSource sessionStopping)
    {
        var cancellationToken = sessionStopping.Token;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HardExitInterval, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var result = await _loop.RunHardExitsAsync(cancellationToken);
                HardExitPassesRun++;

                if (result.AttemptedSymbols.Count > 0)
                {
                    _logger.LogInformation(
                        RunEvents.PositionClosed,
                        "Exit check closed {Submitted} position(s), {Confirmed} confirmed, "
                        + "{Rejected} rejected.",
                        result.Submitted, result.ConfirmedClosed, result.Rejected);
                }
                else
                {
                    // Debug, not information: a pass that finds nothing runs every minute and
                    // would bury the cycle narrative in the operator view.
                    _logger.LogDebug("Exit check found nothing to close.");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AuditPersistenceException error)
            {
                _logger.LogError(error, "The exit check could not write the audit. Stopping the session.");
                _exitLoopFault = error;
                await sessionStopping.CancelAsync();
                return;
            }
            catch (Exception error)
            {
                // One failed check must not end the run. The next one is a minute away.
                _logger.LogError(error, "The exit check failed. Continuing.");
            }
        }
    }

    private async Task RunCycleLoopAsync(CancellationToken cancellationToken)
    {
        var cycles = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = _options.FaultRetryInterval;

            try
            {
                await _loop.InitializeAsync(cancellationToken);
                var clock = await _marketData.GetClockAsync(cancellationToken);

                if (clock.IsOpen || RunOnceIgnoringMarketHours)
                {
                    if (!clock.IsOpen)
                    {
                        _logger.LogWarning(
                            "Market is closed. Running one cycle anyway: quotes are stale and "
                            + "nothing will fill.");
                    }

                    cycles++;
                    CyclesRun = cycles;
                    _logger.LogInformation(
                        RunEvents.CycleStarted,
                        "Cycle {Number} at {At:u}. Market open {Open}.",
                        cycles, _time.GetUtcNow(), clock.IsOpen);

                    var result = await _loop.RunCycleAsync(cancellationToken);

                    _logger.LogInformation(
                        RunEvents.CycleFinished,
                        "Cycle {Number}: {Offered} candidates, {Opened} open order(s), "
                        + "{CloseSubmitted} close order(s), {Closed} confirmed closed, "
                        + "{Rejected} rejected, equity {Equity:N2} USD. Run cost {Cost}.",
                        cycles, result.CandidatesOffered, result.OrdersSubmitted,
                        result.CloseOrdersSubmitted, result.PositionsClosed,
                        result.ActionsRejected, result.Equity,
                        RunningCost());

                    if (RunOnceIgnoringMarketHours)
                    {
                        _logger.LogInformation("Single cycle requested. Stopping.");
                        break;
                    }

                    interval = _options.CycleInterval;
                }
                else
                {
                    _logger.LogInformation(
                        "Market closed. Next open {NextOpen:u}. Stopping to avoid idle model spend.",
                        clock.NextOpenUtc);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (AuditPersistenceException)
            {
                throw;
            }
            catch (Exception error)
            {
                // One bad cycle must not end a four-day run.
                _logger.LogError(error, "Cycle failed. Continuing to the next one.");
            }

            // The wait is half an hour by default. Announce it: without this line the operator
            // view shows nothing for that long, and a live session looks the same as a hung one.
            // `interval` is still the fault retry unless a cycle actually ran, so name which
            // wait this is. Five minutes after a fault and thirty minutes after a cycle must
            // not read alike.
            var afterCycle = interval == _options.CycleInterval;
            _logger.LogInformation(
                RunEvents.CycleWaiting,
                "Waiting {Minutes:N0} minute(s) {Reason}. Next cycle at {ResumeUtc:u}.",
                interval.TotalMinutes,
                afterCycle ? "until the next cycle" : "before retrying after a fault",
                _time.GetUtcNow() + interval);

            try
            {
                await Task.Delay(interval, _time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Live session stopped after {Cycles} cycles.", cycles);
    }
}

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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Live session starting. {Symbols} symbols, {Interval} minute cycle, flatten at {Flatten:u}.",
            _options.TrackedSymbols.Count, _options.CycleInterval.TotalMinutes,
            _riskOptions.CompetitionFlattenUtc);

        var cycles = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = _options.ClosedMarketInterval;

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
                    // The market is shut. Do not spend an agent call, but stay up so the open
                    // needs no restart.
                    _logger.LogInformation(
                        "Market closed. Next open {NextOpen:u}. Waiting.", clock.NextOpenUtc);
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

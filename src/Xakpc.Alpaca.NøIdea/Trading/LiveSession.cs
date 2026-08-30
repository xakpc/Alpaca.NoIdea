using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Trading;

/// <summary>
/// Runs the trading loop against the live paper account until it is stopped.
/// </summary>
/// <remarks>
/// <para>
/// The session owns pacing and restart behaviour; the cycle itself is
/// <see cref="TradingLoop"/>, unchanged from the one replay drives.
/// </para>
/// <para>
/// A cycle that throws does not stop the session. The window is four days and an unattended
/// process that exits on one transient broker error is worse than one that logs it and tries
/// again on the next cycle. A cancellation still stops it immediately.
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
                var clock = await _marketData.GetClockAsync(cancellationToken);

                if (clock.IsOpen)
                {
                    var result = await _loop.RunCycleAsync(cancellationToken);
                    cycles++;

                    _logger.LogInformation(
                        "Cycle {Number}: {Offered} candidates, {Opened} opened, {Closed} closed, "
                        + "{Rejected} rejected, equity {Equity:N2} USD.",
                        cycles, result.CandidatesOffered, result.OrdersSubmitted,
                        result.PositionsClosed, result.ActionsRejected, result.Equity);

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

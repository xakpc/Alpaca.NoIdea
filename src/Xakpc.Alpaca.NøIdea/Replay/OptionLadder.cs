namespace Xakpc.Alpaca.NøIdea.Replay;

/// <summary>One call price on one strike, from the same session and expiration.</summary>
public readonly record struct LadderPoint(decimal Strike, decimal Close);

/// <summary>
/// The market probability reference, read from the slope of a call-price ladder.
/// </summary>
/// <remarks>
/// <para>
/// This answers one question that nothing else in replay can: <b>what probability was the
/// option market itself pricing?</b> Alpaca serves no historical greek and no historical
/// quote, so the usual reference — absolute delta — cannot be rebuilt from history. See
/// <c>.lode/replay/option-data-availability.md</c>.
/// </para>
/// <para>
/// The price of a call falls as the strike rises, and the rate of that fall is the
/// probability of finishing above the strike:
/// </para>
/// <code>
/// P(S > K) = -dC/dK  ~  (C(K_below) - C(K_above)) / (K_above - K_below)
/// </code>
/// <para>
/// No implied volatility, no Black-Scholes solver, no risk-free rate, no dividend assumption.
/// The method was measured over 149,838 questions at Brier 0.13787 with monotonic
/// calibration, and it beat the trained model in every period (ADR-013), so it is validated
/// rather than assumed.
/// </para>
/// <para>
/// <b>The result is a risk-neutral probability, not a real-world one.</b> It carries a drift
/// of the risk-free rate rather than the expected return of the underlying, so it sits
/// slightly below the true chance for an asset with a positive risk premium. Over one to
/// three days the gap is small, but it tilts any comparison in favour of a real-world
/// forecaster — state that wherever a forecast is scored against it.
/// </para>
/// </remarks>
public static class OptionLadder
{
    /// <summary>
    /// The market probability that the underlying finishes above <paramref name="strike"/>,
    /// or null when the ladder cannot answer.
    /// </summary>
    /// <remarks>
    /// Uses the closest strike below and the closest above, so the estimate is a central
    /// difference around the target rather than a one-sided one. Returns null when the ladder
    /// has no bracketing pair, when the two strikes coincide, or when the prices are
    /// non-monotonic enough to push the slope outside [0, 1] — a crossed ladder is a data
    /// fault, and clamping it would hide that.
    /// </remarks>
    public static decimal? ProbabilityAbove(IReadOnlyList<LadderPoint> ladder, decimal strike)
    {
        ArgumentNullException.ThrowIfNull(ladder);

        if (ladder.Count < 2)
        {
            return null;
        }

        LadderPoint? below = null;
        LadderPoint? above = null;

        foreach (var point in ladder)
        {
            if (point.Strike <= strike && (below is null || point.Strike > below.Value.Strike))
            {
                below = point;
            }

            if (point.Strike > strike && (above is null || point.Strike < above.Value.Strike))
            {
                above = point;
            }
        }

        if (below is not { } low || above is not { } high)
        {
            return null;
        }

        var width = high.Strike - low.Strike;
        if (width <= 0m)
        {
            return null;
        }

        var probability = (low.Close - high.Close) / width;

        // A call ladder must fall with the strike, so the slope belongs in [0, 1]. Outside
        // that the input is inconsistent -- a stale print, a one-trade bar -- and a clamped
        // value would look like a real probability. Fail closed instead.
        return probability is < 0m or > 1m ? null : probability;
    }
}

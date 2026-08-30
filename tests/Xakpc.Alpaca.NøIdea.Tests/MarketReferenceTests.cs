using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Replay;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The option-ladder market probability and the contract symbol parser it depends on.
/// </summary>
/// <remarks>
/// The ladder is the only market probability reference replay can rebuild, because Alpaca
/// serves no historical greek. It is also the reference that beat the trained model in every
/// period (ADR-013), so a regression here silently breaks the one measurement that works.
/// </remarks>
public class MarketReferenceTests
{
    // ---------------------------------------------------------------- ladder

    [Fact]
    public void TheSlopeBetweenTwoStrikesIsTheProbabilityAbove()
    {
        // A call at 100 costs 6.00 and a call at 110 costs 1.00. The price falls 5.00 across
        // 10 dollars of strike, so the market prices a 50% chance of finishing above 105.
        LadderPoint[] ladder = [new(100m, 6m), new(110m, 1m)];

        Assert.Equal(0.5m, OptionLadder.ProbabilityAbove(ladder, 105m));
    }

    [Fact]
    public void TheNearestBracketingStrikesAreUsed()
    {
        // The 100/110 pair brackets 105 more tightly than 90/120 does, so it must win.
        LadderPoint[] ladder = [new(90m, 12m), new(100m, 6m), new(110m, 1m), new(120m, 0.2m)];

        Assert.Equal(0.5m, OptionLadder.ProbabilityAbove(ladder, 105m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ALadderTooShortToBracketAnswersNothing(int pointCount)
    {
        var ladder = Enumerable.Range(0, pointCount)
            .Select(index => new LadderPoint(100m + index, 5m))
            .ToArray();

        Assert.Null(OptionLadder.ProbabilityAbove(ladder, 105m));
    }

    [Fact]
    public void AStrikeOutsideTheLadderAnswersNothingRatherThanExtrapolating()
    {
        LadderPoint[] ladder = [new(100m, 6m), new(110m, 1m)];

        Assert.Null(OptionLadder.ProbabilityAbove(ladder, 200m));
    }

    [Fact]
    public void ACrossedLadderFailsClosedInsteadOfClamping()
    {
        // A call cannot cost more at a higher strike. When it does the data is faulty, and a
        // clamped 0 or 1 would look like a confident probability instead of a fault.
        LadderPoint[] ladder = [new(100m, 1m), new(110m, 6m)];

        Assert.Null(OptionLadder.ProbabilityAbove(ladder, 105m));
    }

    [Fact]
    public void ASlopeSteeperThanTheStrikeWidthFailsClosed()
    {
        // A 9.00 fall across 10 dollars is a 0.9 probability, which is valid. An 11.00 fall
        // is not a probability at all.
        LadderPoint[] valid = [new(100m, 10m), new(110m, 1m)];
        LadderPoint[] impossible = [new(100m, 12m), new(110m, 1m)];

        Assert.Equal(0.9m, OptionLadder.ProbabilityAbove(valid, 105m));
        Assert.Null(OptionLadder.ProbabilityAbove(impossible, 105m));
    }

    // ---------------------------------------------------------------- OCC symbols

    [Theory]
    [InlineData("AAPL240119C00180000", "AAPL", 2024, 1, 19, true, 180.0)]
    [InlineData("SPY260621P00500000", "SPY", 2026, 6, 21, false, 500.0)]
    [InlineData("MU260918C00092500", "MU", 2026, 9, 18, true, 92.5)]
    public void AnOccSymbolParsesIntoItsParts(
        string symbol, string underlying, int year, int month, int day, bool isCall, double strike)
    {
        Assert.True(OccOptionSymbol.TryParse(symbol, out var parsed));

        Assert.Equal(underlying, parsed.Underlying);
        Assert.Equal(new DateOnly(year, month, day), parsed.Expiration);
        Assert.Equal(isCall, parsed.IsCall);
        Assert.Equal((decimal)strike, parsed.Strike);
    }

    [Fact]
    public void AHalfDollarStrikeIsExact()
    {
        // The strike divides by 1000 in decimal, never in a binary float, so 92.5 is exactly
        // 92.5 and a strike comparison in the ladder cannot drift.
        Assert.True(OccOptionSymbol.TryParse("MU260918C00092500", out var parsed));
        Assert.Equal(92.5m, parsed.Strike);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAPL")]
    [InlineData("AAPL240119X00180000")]  // not a call or a put
    [InlineData("AAPL241332C00180000")]  // month 13, day 32
    public void AMalformedSymbolIsRejectedRatherThanGuessed(string? symbol)
    {
        Assert.False(OccOptionSymbol.TryParse(symbol, out _));
    }
}

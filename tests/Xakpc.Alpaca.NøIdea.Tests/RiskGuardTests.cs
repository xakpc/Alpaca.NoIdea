using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The limits an agent cannot talk its way past.
/// </summary>
/// <remarks>
/// The strategy is agent-directed: the agent picks direction, contract, and size. These tests
/// pin the other half of that bargain — that every one of its requests is checked by
/// deterministic C# against <see cref="RiskOptions"/>, which no agent output can modify
/// (ADR-006). Each test is one rule from .lode/trading/risk-guardrails.md.
/// </remarks>
public class RiskGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 14, 0, 0, TimeSpan.Zero);

    private static readonly RiskOptions Options = new()
    {
        // Far past the test dates, so the competition flatten does not fire in these tests.
        CompetitionFlattenUtc = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero),
    };

    private static RiskGuard Guard() => new(Options, new FakeClock(Now));

    private static RiskSnapshot Healthy => new()
    {
        Equity = 100_000m,
        DayOpeningEquity = 100_000m,
        OpenPositions = 0,
        OpenPositionCost = 0m,
        PositionsOpenedToday = 0,
    };

    // ---------------------------------------------------------------- sizing

    [Fact]
    public void APositionLargerThanThePerTradeCapIsRejected()
    {
        // 2% of 100,000 is 2,000. A 25.00 premium is 2,500 for one contract.
        var verdict = Guard().CanOpen(
            Open(contracts: 1), Candidate(price: 25m), Healthy, new StrategyPolicy());

        Assert.False(verdict.Allowed);
        Assert.Contains("exceeds", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentAskingForMoreContractsThanThePolicyAllowsIsRejected()
    {
        var verdict = Guard().CanOpen(
            Open(contracts: 4), Candidate(price: 1m), Healthy,
            new StrategyPolicy { MaxContractsPerTrade = 1 });

        Assert.False(verdict.Allowed);
        Assert.Contains("per-trade limit", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyCannotRaiseTheContractLimitAboveTheHardBound()
    {
        // The agent writes the policy, so it will try. ClampTo is what stops it.
        var clamped = new StrategyPolicy { MaxContractsPerTrade = 500 }.ClampTo(Options);

        Assert.Equal(Options.HardMaxContractsPerTrade, clamped.MaxContractsPerTrade);
    }

    [Fact]
    public void APolicyCannotWidenTheExpirationWindowBeyondTheHardBound()
    {
        var clamped = new StrategyPolicy
        {
            MinDaysToExpiration = 0,
            MaxDaysToExpiration = 365,
        }.ClampTo(Options);

        Assert.Equal(Options.HardMinDaysToExpiration, clamped.MinDaysToExpiration);
        Assert.Equal(Options.HardMaxDaysToExpiration, clamped.MaxDaysToExpiration);
    }

    [Fact]
    public void AnInvertedPolicyWindowIsRepairedRatherThanLeftToSelectNothing()
    {
        var clamped = new StrategyPolicy
        {
            MinDaysToExpiration = 10,
            MaxDaysToExpiration = 3,
            MinMarketProbability = 0.9m,
            MaxMarketProbability = 0.1m,
        }.ClampTo(Options);

        Assert.True(clamped.MinDaysToExpiration <= clamped.MaxDaysToExpiration);
        Assert.True(clamped.MinMarketProbability <= clamped.MaxMarketProbability);
    }

    [Fact]
    public void APolicyCannotSetAZeroStopWhichWouldCloseEverythingImmediately()
    {
        var clamped = new StrategyPolicy
        {
            TakeProfitFraction = 0m,
            StopLossFraction = 0m,
        }.ClampTo(Options);

        Assert.True(clamped.TakeProfitFraction > 0m);
        Assert.True(clamped.StopLossFraction > 0m);
    }

    // ---------------------------------------------------------------- exposure

    [Fact]
    public void TotalExposureAboveTheAccountCapIsRejected()
    {
        var snapshot = Healthy with { OpenPositionCost = 9_900m };

        var verdict = Guard().CanOpen(
            Open(contracts: 1), Candidate(price: 5m), snapshot, new StrategyPolicy());

        Assert.False(verdict.Allowed);
        Assert.Contains("total exposure", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConcurrentPositionLimitIsEnforced()
    {
        var snapshot = Healthy with { OpenPositions = Options.MaxConcurrentPositions };

        Assert.False(Guard().CanOpen(
            Open(), Candidate(), snapshot, new StrategyPolicy()).Allowed);
    }

    [Fact]
    public void TheDailyNewPositionLimitIsEnforced()
    {
        var snapshot = Healthy with { PositionsOpenedToday = Options.MaxNewPositionsPerDay };

        Assert.False(Guard().CanOpen(
            Open(), Candidate(), snapshot, new StrategyPolicy()).Allowed);
    }

    // ---------------------------------------------------------------- circuit breaker

    [Fact]
    public void TheDailyLossLimitHaltsNewPositions()
    {
        // Down 6% on the day, against a 5% limit.
        var snapshot = Healthy with { Equity = 94_000m, DayOpeningEquity = 100_000m };

        Assert.True(Guard().NewPositionsHalted(snapshot));
        Assert.False(Guard().CanOpen(
            Open(), Candidate(), snapshot, new StrategyPolicy()).Allowed);
    }

    [Fact]
    public void AnUnknownDailyBaselineHaltsTradingRatherThanAssumingItIsFine()
    {
        var snapshot = Healthy with { DayOpeningEquity = 0m };

        Assert.True(Guard().NewPositionsHalted(snapshot));
    }

    // ---------------------------------------------------------------- quote quality

    [Fact]
    public void AOneSidedQuoteIsRejected()
    {
        var candidate = Candidate() with
        {
            Candidate = Contract() with
            {
                Quality = QuoteQuality.OneSided,
                Bid = 1m,
                Ask = null,
            },
        };

        Assert.False(Guard().CheckContract(candidate, new StrategyPolicy()).Allowed);
    }

    [Fact]
    public void AWideSpreadIsRejected()
    {
        var candidate = Candidate() with
        {
            Candidate = Contract() with { Bid = 1.00m, Ask = 2.00m, ReferencePrice = 2.00m },
        };

        var verdict = Guard().CheckContract(candidate, new StrategyPolicy());

        Assert.False(verdict.Allowed);
        Assert.Contains("spread", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleQuoteIsRejected()
    {
        var candidate = Candidate() with
        {
            Candidate = Contract() with { QuoteTimestampUtc = Now.AddHours(-2) },
        };

        var verdict = Guard().CheckContract(candidate, new StrategyPolicy());

        Assert.False(verdict.Allowed);
        Assert.Contains("old", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractExpiringAfterTheCompetitionMeasurementPointIsRejected()
    {
        var guard = new RiskGuard(
            new RiskOptions { CompetitionFlattenUtc = new DateTimeOffset(2026, 9, 3, 19, 30, 0, TimeSpan.Zero) },
            new FakeClock(Now));

        var candidate = Candidate() with
        {
            Candidate = Contract() with { Expiration = new DateOnly(2026, 9, 18) },
        };

        var verdict = guard.CheckContract(candidate, new StrategyPolicy { MaxDaysToExpiration = 21 });

        Assert.False(verdict.Allowed);
    }

    // ---------------------------------------------------------------- exits

    [Fact]
    public void TakeProfitAndStopLossFireAtThePolicyLevels()
    {
        var guard = Guard();
        var policy = new StrategyPolicy { TakeProfitFraction = 0.50m, StopLossFraction = 0.40m };
        var position = new PositionState
        {
            Symbol = "TEST260904C00100000",
            Quantity = 1,
            AverageEntryPrice = 2.00m,
        };

        Assert.Contains("take profit", guard.MandatoryExitReason(position, policy, 3.10m)!, StringComparison.Ordinal);
        Assert.Contains("stop loss", guard.MandatoryExitReason(position, policy, 1.10m)!, StringComparison.Ordinal);
        Assert.Null(guard.MandatoryExitReason(position, policy, 2.10m));
    }

    [Fact]
    public void APositionWithNoPriceIsHeldRatherThanClosedBlindly()
    {
        var position = new PositionState
        {
            Symbol = "TEST260904C00100000",
            Quantity = 1,
            AverageEntryPrice = 2.00m,
        };

        Assert.Null(Guard().MandatoryExitReason(position, new StrategyPolicy(), currentPrice: null));
    }

    [Fact]
    public void EverythingClosesAtTheCompetitionFlattenTime()
    {
        // Thursday 2026-09-03 EOD is the effective final portfolio state, so the exit is not
        // the agent's to override.
        var guard = new RiskGuard(
            new RiskOptions { CompetitionFlattenUtc = Now.AddMinutes(-1) }, new FakeClock(Now));

        var position = new PositionState
        {
            Symbol = "TEST260904C00100000",
            Quantity = 1,
            AverageEntryPrice = 2.00m,
        };

        Assert.Equal("competition flatten", guard.MandatoryExitReason(position, new StrategyPolicy(), 2.00m));
    }

    // ---------------------------------------------------------------- helpers

    private static StrategyAction Open(int contracts = 1) => new()
    {
        Kind = StrategyActionKind.OpenCall,
        ContractSymbol = "TEST260904C00100000",
        Contracts = contracts,
        Reasoning = "test",
    };

    private static OptionCandidate Contract() => new()
    {
        ContractSymbol = "TEST260904C00100000",
        Underlying = "TEST",
        OptionType = "call",
        Strike = 100m,
        Expiration = new DateOnly(2026, 9, 4),
        Quality = QuoteQuality.TwoSided,
        ReferencePrice = 1.00m,
        Bid = 0.98m,
        Ask = 1.00m,
        QuoteTimestampUtc = Now.AddMinutes(-1),
    };

    private static CandidateView Candidate(decimal price = 1.00m) => new()
    {
        Candidate = Contract() with { ReferencePrice = price, Bid = price * 0.98m, Ask = price },
        UnderlyingPrice = 100m,
        MarketProbability = 0.5m,
        RecentNewsCount = 3,
    };
}

/// <summary>A <see cref="TimeProvider"/> pinned to one instant.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

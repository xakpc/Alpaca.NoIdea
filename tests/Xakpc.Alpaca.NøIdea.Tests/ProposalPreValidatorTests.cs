using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

public class ProposalPreValidatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStaleQuoteIsRejectedByDefault()
    {
        var rejection = Validator().Validate(Operation(), Request(Candidate(
            Contract() with { QuoteTimestampUtc = Now.AddHours(-2) })));

        Assert.Equal("REJECT_BAD_QUOTE", rejection);
    }

    [Fact]
    public void AllowStaleQuotesLetsReviewersReasonAboutAnOtherwiseValidQuote()
    {
        var rejection = Validator(allowStaleQuotes: true).Validate(Operation(), Request(Candidate(
            Contract() with { QuoteTimestampUtc = Now.AddHours(-2) })));

        Assert.Null(rejection);
    }

    [Fact]
    public void AllowStaleQuotesStillRejectsAMissingTimestamp()
    {
        var rejection = Validator(allowStaleQuotes: true).Validate(Operation(), Request(Candidate(
            Contract() with { QuoteTimestampUtc = null })));

        Assert.Equal("REJECT_BAD_QUOTE", rejection);
    }

    [Fact]
    public void AllowStaleQuotesStillRejectsOtherQuoteQualityFailures()
    {
        var rejection = Validator(allowStaleQuotes: true).Validate(Operation(), Request(Candidate(
            Contract() with { Quality = QuoteQuality.OneSided, Ask = null })));

        Assert.Equal("REJECT_BAD_QUOTE", rejection);
    }

    private static ProposalPreValidator Validator(bool allowStaleQuotes = false) =>
        new(
            new RiskOptions { AllowStaleQuotes = allowStaleQuotes },
            ["TEST"],
            new FakeClock(Now));

    // ---------------------------------------------------------------- stated numbers

    [Fact]
    public void AProposalWithNoStatedNumbersIsUnaffected()
    {
        // The fields are optional on purpose. An omitted claim must never reject.
        Assert.Null(Validator().Validate(Operation(), Request(Candidate(Contract()))));
    }

    [Fact]
    public void StatedNumbersThatMatchTheCatalogPass()
    {
        var rejection = Validator().Validate(
            Operation(new MarketClaims
            {
                QuotedBid = 0.98m,
                QuotedAsk = 1.00m,
                UnderlyingLast = 100m,
            }),
            Request(Candidate(Contract())));

        Assert.Null(rejection);
    }

    [Fact]
    public void RoundingInsideOnePercentPasses()
    {
        var rejection = Validator().Validate(
            Operation(new MarketClaims { QuotedAsk = 1.005m, UnderlyingLast = 100.5m }),
            Request(Candidate(Contract())));

        Assert.Null(rejection);
    }

    [Fact]
    public void AFabricatedQuoteIsRejectedBeforeTheRoomReadsIt()
    {
        var rejection = Validator().Validate(
            Operation(new MarketClaims { QuotedAsk = 0.40m }),
            Request(Candidate(Contract())));

        Assert.Equal("REJECT_FABRICATED_QUOTE", rejection);
    }

    [Fact]
    public void AFabricatedUnderlyingPriceIsRejected()
    {
        // The 2026-08-31 AMZN proposal argued from a price path that had not happened.
        var rejection = Validator().Validate(
            Operation(new MarketClaims { UnderlyingLast = 257.18m }),
            Request(Candidate(Contract())));

        Assert.Equal("REJECT_FABRICATED_QUOTE", rejection);
    }

    [Fact]
    public void AClaimTheCatalogCannotAnswerPasses()
    {
        // Missing delta and implied volatility are ordinary on the Indicative feed. An absent
        // catalog value is not evidence that the proposer invented anything.
        var rejection = Validator().Validate(
            Operation(new MarketClaims { Delta = 0.45m, ImpliedVolatility = 0.58m }),
            Request(Candidate(Contract() with { Delta = null, ImpliedVolatility = null })));

        Assert.Null(rejection);
    }

    [Fact]
    public void AStatedDeltaThatDisagreesWithTheCatalogIsRejected()
    {
        var rejection = Validator().Validate(
            Operation(new MarketClaims { Delta = 0.80m }),
            Request(Candidate(Contract() with { Delta = 0.46m })));

        Assert.Equal("REJECT_FABRICATED_QUOTE", rejection);
    }

    // ---------------------------------------------------------------- helpers

    private static ProposedOperation Operation(MarketClaims? claims = null) => new()
    {
        Actions =
        [
            new StrategyAction
            {
                Kind = StrategyActionKind.OpenCall,
                ContractSymbol = "TEST260904C00100000",
                Contracts = 1,
                ProfitProbability = 0.6m,
                Reasoning = "test",
                Claims = claims,
            },
        ],
        Thesis = "test thesis",
    };

    private static WarRoomRequest Request(TradeableContractView candidate) => new()
    {
        ProposalId = "proposal-test-stale-quote",
        Mode = "dry-run",
        Purpose = WarRoomPurpose.NewTrade,
        AllowedActions = [StrategyActionKind.OpenCall, StrategyActionKind.OpenPut],
        Market = new StrategyContext
        {
            NowUtc = Now,
            Account = new AccountState
            {
                AccountNumber = "TEST",
                Equity = 100_000m,
                Cash = 100_000m,
                BuyingPower = 100_000m,
                IsTradingBlocked = false,
                IsAccountBlocked = false,
            },
            Positions = [],
            ContractCatalog = [candidate],
            Policy = new StrategyPolicy(),
            RemainingPositionSlots = 4,
            NewPositionsHalted = false,
        },
    };

    private static OptionCandidate Contract() => new()
    {
        ContractSymbol = "TEST260904C00100000",
        Underlying = "TEST",
        OptionType = "call",
        Strike = 100m,
        Expiration = new DateOnly(2026, 9, 4),
        Quality = QuoteQuality.TwoSided,
        Bid = 0.98m,
        Ask = 1.00m,
        QuoteTimestampUtc = Now.AddMinutes(-1),
    };

    private static TradeableContractView Candidate(OptionCandidate contract) => new()
    {
        Contract = contract,
        UnderlyingPrice = 100m,
    };
}

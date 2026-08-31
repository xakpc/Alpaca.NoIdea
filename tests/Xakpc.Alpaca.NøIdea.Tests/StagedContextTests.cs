using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

public class StagedContextTests
{
    [Fact]
    public void HeadlinesDeduplicateArticlesCoverSymbolsAndCapTheRecencyFill()
    {
        var start = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var news = new[]
        {
            News(1, start, ["TSLA", "META"]),
            News(1, start.AddMinutes(-1), ["TSLA", "META"]),
            News(2, start.AddMinutes(-2), ["TSLA"]),
            News(3, start.AddMinutes(-3), ["TSLA"]),
            News(4, start.AddMinutes(-4), ["TSLA"]),
            News(5, start.AddMinutes(-5), ["NVDA"]),
            News(6, start.AddMinutes(-6), ["AAPL"]),
        };

        var selected = HeadlineIndexSelector.Select(
            news, ["TSLA", "META", "NVDA", "AAPL"], limit: 6, maxPerSymbol: 3);

        Assert.Equal(selected.Count, selected.Select(item => item.Id).Distinct().Count());
        Assert.All(new[] { "TSLA", "META", "NVDA", "AAPL" }, symbol =>
            Assert.Contains(selected, item => item.Symbols.Contains(symbol)));
        Assert.DoesNotContain(selected, item => item.Id == 4);
        Assert.Equal(start, selected[0].PublishedUtc);
    }

    [Fact]
    public void ReviewerNeighborhoodHasTwoStrikesEachWayAndAdjacentExpirations()
    {
        var expiration = new DateOnly(2026, 9, 4);
        var catalog = new List<TradeableContractView>();
        foreach (var strike in new[] { 90m, 95m, 100m, 105m, 110m })
        {
            catalog.Add(View(strike, expiration));
        }

        catalog.Add(View(99m, expiration.AddDays(-1)));
        catalog.Add(View(101m, expiration.AddDays(1)));

        var operation = new ProposedOperation
        {
            Thesis = "test",
            Actions =
            [
                new StrategyAction
                {
                    Kind = StrategyActionKind.OpenCall,
                    ContractSymbol = Symbol(100m, expiration),
                    Reasoning = "test",
                },
            ],
        };

        var nearby = ReviewContextSelector.NearbyContracts(Context(catalog), operation);

        Assert.Equal(7, nearby.Count);
        Assert.Contains(nearby, item => item.Contract.Strike == 90m);
        Assert.Contains(nearby, item => item.Contract.Strike == 110m);
        Assert.Contains(nearby, item => item.Contract.Expiration == expiration.AddDays(-1));
        Assert.Contains(nearby, item => item.Contract.Expiration == expiration.AddDays(1));
    }

    private static NewsItem News(long id, DateTimeOffset at, string[] symbols) =>
        new(id, at, $"headline-{id}", null, "test", symbols);

    private static TradeableContractView View(decimal strike, DateOnly expiration) => new()
    {
        Contract = new OptionCandidate
        {
            ContractSymbol = Symbol(strike, expiration),
            Underlying = "NVDA",
            OptionType = "call",
            Strike = strike,
            Expiration = expiration,
            Quality = QuoteQuality.TwoSided,
            ReferencePrice = 1m,
            Bid = 0.95m,
            Ask = 1m,
        },
        UnderlyingPrice = 100m,
    };

    private static string Symbol(decimal strike, DateOnly expiration) =>
        $"NVDA{expiration:yyMMdd}C{strike:00000000}";

    private static StrategyContext Context(IReadOnlyList<TradeableContractView> catalog) => new()
    {
        NowUtc = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
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
        ContractCatalog = catalog,
        Policy = new StrategyPolicy(),
        RemainingPositionSlots = 4,
        NewPositionsHalted = false,
    };
}

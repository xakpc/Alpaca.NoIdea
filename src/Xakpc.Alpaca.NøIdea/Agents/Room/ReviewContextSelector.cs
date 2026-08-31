using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>Builds the small, deterministic market neighborhood every reviewer receives.</summary>
public static class ReviewContextSelector
{
    public static IReadOnlyList<TradeableContractView> NearbyContracts(
        StrategyContext market, ProposedOperation operation)
    {
        var symbol = operation.Actions
            .FirstOrDefault(action => action.Kind is StrategyActionKind.OpenCall or StrategyActionKind.OpenPut)
            ?.ContractSymbol;

        var proposed = market.ContractCatalog.FirstOrDefault(view =>
            string.Equals(view.Contract.ContractSymbol, symbol, StringComparison.Ordinal));

        if (proposed is null)
        {
            return [];
        }

        var contract = proposed.Contract;
        var sameKind = market.ContractCatalog
            .Where(view => string.Equals(
                view.Contract.Underlying, contract.Underlying, StringComparison.Ordinal))
            .Where(view => string.Equals(
                view.Contract.OptionType, contract.OptionType, StringComparison.Ordinal))
            .ToArray();

        var selected = new Dictionary<string, TradeableContractView>(StringComparer.Ordinal)
        {
            [contract.ContractSymbol] = proposed,
        };

        foreach (var view in sameKind
                     .Where(view => view.Contract.Expiration == contract.Expiration
                                    && view.Contract.Strike < contract.Strike)
                     .OrderByDescending(view => view.Contract.Strike)
                     .Take(2))
        {
            selected[view.Contract.ContractSymbol] = view;
        }

        foreach (var view in sameKind
                     .Where(view => view.Contract.Expiration == contract.Expiration
                                    && view.Contract.Strike > contract.Strike)
                     .OrderBy(view => view.Contract.Strike)
                     .Take(2))
        {
            selected[view.Contract.ContractSymbol] = view;
        }

        var prior = sameKind
            .Where(view => view.Contract.Expiration < contract.Expiration)
            .Select(view => view.Contract.Expiration)
            .Distinct()
            .OrderDescending()
            .FirstOrDefault();

        var next = sameKind
            .Where(view => view.Contract.Expiration > contract.Expiration)
            .Select(view => view.Contract.Expiration)
            .Distinct()
            .Order()
            .FirstOrDefault();

        AddNearest(prior);
        AddNearest(next);

        return [.. selected.Values
            .OrderBy(view => view.Contract.Expiration)
            .ThenBy(view => view.Contract.Strike)
            .ThenBy(view => view.Contract.ContractSymbol, StringComparer.Ordinal)];

        void AddNearest(DateOnly expiration)
        {
            if (expiration == default)
            {
                return;
            }

            var nearest = sameKind
                .Where(view => view.Contract.Expiration == expiration)
                .OrderBy(view => Math.Abs(view.Contract.Strike - contract.Strike))
                .ThenBy(view => view.Contract.Strike)
                .FirstOrDefault();

            if (nearest is not null)
            {
                selected[nearest.Contract.ContractSymbol] = nearest;
            }
        }
    }

    public static IReadOnlyList<UnderlyingSnapshot> RelevantUnderlyings(
        StrategyContext market, ProposedOperation operation)
    {
        var proposedSymbol = operation.Actions
            .Select(action => action.ContractSymbol)
            .FirstOrDefault(symbol => symbol is not null);
        var proposed = market.ContractCatalog.FirstOrDefault(view =>
            string.Equals(view.Contract.ContractSymbol, proposedSymbol, StringComparison.Ordinal));

        var wanted = new HashSet<string>(StringComparer.Ordinal) { "SPY", "QQQ" };
        if (proposed is not null)
        {
            wanted.Add(proposed.Contract.Underlying);
        }

        var held = market.PortfolioPositions.FirstOrDefault(view => operation.Actions.Any(action =>
            string.Equals(action.ContractSymbol, view.Position.Symbol, StringComparison.Ordinal)));
        if (!string.IsNullOrWhiteSpace(held?.Underlying))
        {
            wanted.Add(held.Underlying);
        }

        return [.. market.Underlyings.Where(snapshot => wanted.Contains(snapshot.Symbol))];
    }

    public static IReadOnlyList<NewsItem> RelevantHeadlines(
        StrategyContext market, ProposedOperation operation)
    {
        var symbols = RelevantUnderlyings(market, operation)
            .Select(snapshot => snapshot.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        return [.. market.News.Where(item => item.Symbols.Any(symbols.Contains))];
    }
}

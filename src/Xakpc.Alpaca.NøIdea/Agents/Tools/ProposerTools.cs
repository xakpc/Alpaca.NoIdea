using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using ToonFormat;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents.Tools;

/// <summary>
/// Creates the local tools that the proposer uses to return structured answers and query
/// the immutable contract catalog.
/// </summary>
internal sealed class ProposerTools(ILogger logger, TradingOptions tradingOptions)
{
    internal const string ProposeToolName = "submit_proposal";
    internal const string RebuttalToolName = "submit_rebuttal";

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TradingOptions _tradingOptions = tradingOptions
        ?? throw new ArgumentNullException(nameof(tradingOptions));

    internal AIFunction CreateProposalTool(
        IReadOnlySet<string> offered,
        IReadOnlySet<string> held,
        IReadOnlyList<StrategyActionKind> allowedActions,
        Action<ProposedOperation> capture) =>
        CreateOperationTool(
            offered,
            held,
            allowedActions,
            capture,
            ProposeToolName,
            "Submit your single best proposal, or NO_TRADE. Call this exactly once, last.",
            "Proposal recorded.");

    internal AIFunction CreateRebuttalTool(
        IReadOnlySet<string> offered,
        IReadOnlySet<string> held,
        IReadOnlyList<StrategyActionKind> allowedActions,
        Action<ProposedOperation> capture) =>
        CreateOperationTool(
            offered,
            held,
            allowedActions,
            capture,
            RebuttalToolName,
            "Defend, modify, or withdraw your proposal. Call this exactly once, last.",
            "Rebuttal recorded.");

    internal AIFunction CreateCatalogTool(StrategyContext market)
    {
        ArgumentNullException.ThrowIfNull(market);

        return AIFunctionFactory.Create(
            (CatalogQueryArguments arguments) => QueryCatalog(market.ContractCatalog, arguments),
            "get_tradeable_contracts",
            "Query the immutable, mechanically validated contract catalog for this cycle. "
            + "This tool performs no Alpaca or network call.");
    }

    internal CatalogContractRow[] ContractRows(IEnumerable<TradeableContractView> catalog) =>
        catalog.Select(view => new CatalogContractRow(
            view.Contract.ContractSymbol,
            view.Contract.Underlying,
            view.Contract.OptionType,
            view.Contract.Strike,
            view.Contract.Expiration,
            view.Contract.Bid,
            view.Contract.Ask,
            view.Contract.Delta,
            view.Contract.ImpliedVolatility,
            view.CostPerContract)).ToArray();

    internal CatalogIndexRow[] CatalogIndex(IEnumerable<TradeableContractView> catalog) => catalog
        .GroupBy(view => view.Contract.Underlying, StringComparer.Ordinal)
        .Select(group => new CatalogIndexRow(
            group.Key,
            group.Count(view => view.Contract.OptionType == "call"),
            group.Count(view => view.Contract.OptionType == "put"),
            group.Min(view => view.Contract.Expiration),
            group.Max(view => view.Contract.Expiration),
            group.Min(view => view.Contract.Strike),
            group.Max(view => view.Contract.Strike)))
        .ToArray();

    private AIFunction CreateOperationTool(
        IReadOnlySet<string> offered,
        IReadOnlySet<string> held,
        IReadOnlyList<StrategyActionKind> allowedActions,
        Action<ProposedOperation> capture,
        string name,
        string description,
        string confirmation)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(allowedActions);
        ArgumentNullException.ThrowIfNull(capture);

        return AIFunctionFactory.Create(
            (ProposalArguments arguments) =>
            {
                capture(ToOperation(arguments, offered, held, allowedActions));
                return confirmation;
            },
            name,
            description);
    }

    private string QueryCatalog(
        IReadOnlyList<TradeableContractView> catalog, CatalogQueryArguments arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments.Underlying)
            || !_tradingOptions.TrackedSymbols.Contains(arguments.Underlying, StringComparer.Ordinal))
        {
            return Toon.Encode(new { error = "unknown underlying" });
        }

        var optionType = arguments.OptionType?.Trim().ToLowerInvariant();
        if (optionType is not null and not "call" and not "put")
        {
            return Toon.Encode(new { error = "option_type must be call or put" });
        }

        DateOnly? expiration = null;
        if (arguments.Expiration is { Length: > 0 } expirationText)
        {
            if (!DateOnly.TryParseExact(
                    expirationText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return Toon.Encode(new { error = "expiration must use yyyy-MM-dd" });
            }

            expiration = parsed;
        }

        if (arguments.StrikeFrom is <= 0m || arguments.StrikeTo is <= 0m)
        {
            return Toon.Encode(new { error = "strike bounds must be positive" });
        }

        if (arguments.StrikeFrom is { } from && arguments.StrikeTo is { } to && from > to)
        {
            return Toon.Encode(new { error = "strike_from must not exceed strike_to" });
        }

        var offset = arguments.Offset ?? 0;
        if (offset < 0)
        {
            return Toon.Encode(new { error = "offset must not be negative" });
        }

        var filtered = catalog
            .Where(view => string.Equals(
                view.Contract.Underlying, arguments.Underlying, StringComparison.Ordinal))
            .Where(view => optionType is null
                           || string.Equals(view.Contract.OptionType, optionType, StringComparison.Ordinal))
            .Where(view => expiration is null || view.Contract.Expiration == expiration)
            .Where(view => arguments.StrikeFrom is null || view.Contract.Strike >= arguments.StrikeFrom)
            .Where(view => arguments.StrikeTo is null || view.Contract.Strike <= arguments.StrikeTo)
            .ToArray();

        if (offset > filtered.Length)
        {
            return Toon.Encode(new { error = "offset is outside the result set", total = filtered.Length });
        }

        var pageSize = Math.Max(1, _tradingOptions.CatalogToolPageSize);
        var page = filtered.Skip(offset).Take(pageSize).ToArray();
        var nextOffset = offset + page.Length < filtered.Length ? offset + page.Length : (int?)null;

        return Toon.Encode(new
        {
            total = filtered.Length,
            offset,
            returned = page.Length,
            next_offset = nextOffset,
            contracts = ContractRows(page),
        });
    }

    private ProposedOperation ToOperation(
        ProposalArguments arguments,
        IReadOnlySet<string> offered,
        IReadOnlySet<string> held,
        IReadOnlyList<StrategyActionKind> allowedActions)
    {
        if (arguments.NoTrade == true || arguments.Actions is null || arguments.Actions.Count == 0)
        {
            return ProposedOperation.Nothing(
                string.IsNullOrWhiteSpace(arguments.Thesis) ? "NO_TRADE" : arguments.Thesis!);
        }

        var actions = new List<StrategyAction>();

        foreach (var item in arguments.Actions)
        {
            var kind = item.Action?.ToLowerInvariant() switch
            {
                "open_call" => StrategyActionKind.OpenCall,
                "open_put" => StrategyActionKind.OpenPut,
                "close" or "close_position" => StrategyActionKind.ClosePosition,
                _ => StrategyActionKind.Hold,
            };

            if (kind == StrategyActionKind.Hold || !allowedActions.Contains(kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ContractSymbol))
            {
                continue;
            }

            var known = kind == StrategyActionKind.ClosePosition ? held : offered;
            if (!known.Contains(item.ContractSymbol))
            {
                _logger.LogWarning(
                    "The proposer named {Symbol}, which was not available this cycle. Dropped.",
                    item.ContractSymbol);
                continue;
            }

            actions.Add(new StrategyAction
            {
                Kind = kind,
                ContractSymbol = item.ContractSymbol,
                Contracts = Math.Max(1, item.Contracts ?? 1),
                Probability = item.Probability is { } probability
                    ? Math.Clamp(probability, 0m, 1m)
                    : null,
                Reasoning = string.IsNullOrWhiteSpace(item.Reasoning)
                    ? arguments.Thesis ?? "(no reasoning given)"
                    : item.Reasoning,
            });
        }

        if (actions.Count == 0)
        {
            return ProposedOperation.Nothing("nothing survived validation");
        }

        if (actions.Count > 1)
        {
            _logger.LogWarning(
                "The proposer submitted {Count} actions. Only the first can enter the room.",
                actions.Count);
            actions.RemoveRange(1, actions.Count - 1);
        }

        return new ProposedOperation
        {
            Actions = actions,
            Thesis = string.IsNullOrWhiteSpace(arguments.Thesis) ? "(no thesis given)" : arguments.Thesis!,
            ThesisConditions = arguments.ThesisConditions ?? [],
            MainRisks = arguments.MainRisks ?? [],
        };
    }

    internal sealed record CatalogContractRow(
        string Symbol,
        string Underlying,
        string Type,
        decimal Strike,
        DateOnly Expiration,
        decimal? Bid,
        decimal? Ask,
        decimal? Delta,
        decimal? ImpliedVolatility,
        decimal CostUsd);

    internal sealed record CatalogIndexRow(
        string Underlying,
        int Calls,
        int Puts,
        DateOnly ExpirationFrom,
        DateOnly ExpirationTo,
        decimal StrikeFrom,
        decimal StrikeTo);

    [Description("Your trade proposal, or NO_TRADE.")]
    public sealed class ProposalArguments
    {
        [Description("True when no trade is worth making. Leave actions empty.")]
        public bool? NoTrade { get; set; }

        [Description("Why, in a short statement. This is what the room debates.")]
        public string? Thesis { get; set; }

        [Description("What must stay true for the thesis to hold. Each must be checkable later.")]
        public List<string>? ThesisConditions { get; set; }

        [Description("The main ways this trade loses.")]
        public List<string>? MainRisks { get; set; }

        [Description("The operations to carry out. One trade at a time.")]
        public List<ProposalAction>? Actions { get; set; }
    }

    public sealed class ProposalAction
    {
        [Description("One of: open_call, open_put, close.")]
        public string? Action { get; set; }

        [Description("The exact contract symbol from candidates, or an open position to close.")]
        public string? ContractSymbol { get; set; }

        [Description("How many contracts you want. The room and the risk rules may reduce it.")]
        public int? Contracts { get; set; }

        [Description("Your honest probability from 0 to 1 that this finishes profitable.")]
        public decimal? Probability { get; set; }

        [Description("Why this contract specifically.")]
        public string? Reasoning { get; set; }
    }

    public sealed class CatalogQueryArguments
    {
        [Description("Required tracked underlying symbol, for example SPY or NVDA.")]
        [JsonPropertyName("underlying")]
        public required string Underlying { get; set; }

        [Description("Optional call or put filter.")]
        [JsonPropertyName("option_type")]
        public string? OptionType { get; set; }

        [Description("Optional expiration in yyyy-MM-dd form.")]
        [JsonPropertyName("expiration")]
        public string? Expiration { get; set; }

        [Description("Optional inclusive minimum strike.")]
        [JsonPropertyName("strike_from")]
        public decimal? StrikeFrom { get; set; }

        [Description("Optional inclusive maximum strike.")]
        [JsonPropertyName("strike_to")]
        public decimal? StrikeTo { get; set; }

        [Description("Optional zero-based result offset. Results contain at most 200 rows.")]
        [JsonPropertyName("offset")]
        public int? Offset { get; set; }
    }
}

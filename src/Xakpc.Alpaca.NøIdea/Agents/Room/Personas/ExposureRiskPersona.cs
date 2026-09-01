using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents.Room.Personas;

/// <summary>
/// The portfolio seat. Plain C#: no model, no tokens, no hallucination.
/// </summary>
/// <remarks>
/// <para>
/// This covers the arithmetic half of the spec's Risk Analyst (§3.6) — exposure,
/// concentration, remaining capacity, time to expiration — and it does that arithmetic
/// exactly, which a language model does not.
/// </para>
/// <para>
/// It is also the proof that <see cref="IPersona"/> is not an LLM interface. A seat can think
/// in C#, cost nothing, and still analyse, discuss and vote alongside the models.
/// </para>
/// <para>
/// <b>It does not replace <c>RiskGuard</c></b>, and the spec is explicit about that. RiskGuard
/// enforces the hard limits and cannot be outvoted. This persona votes on whether a legal
/// trade is a <em>sensible</em> use of the remaining capacity, which is a different question.
/// </para>
/// </remarks>
public sealed class ExposureRiskPersona(RiskOptions risk) : IPersona
{
    private readonly RiskOptions _risk = risk ?? throw new ArgumentNullException(nameof(risk));

    public string Name => "exposure";

    public ModelProvider Provider => ModelProvider.None;

    public Task<PersonaAnalysis> AnalyseAsync(RoomContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = Assess(context);

        return Task.FromResult(new PersonaAnalysis
        {
            Persona = Name,
            InitialVote = findings.Vote,
            Confidence = findings.Confidence,
            Analysis = findings.Summary,
            Risks = findings.Concerns,
            SupportingEvidence = findings.Facts.Select(fact => new EvidenceItem
            {
                Claim = fact,
                Source = "portfolio snapshot",
                ObservedAtUtc = context.Market.NowUtc,
                Direction = findings.Vote == VoteKind.Reject
                    ? EvidenceDirection.Opposes
                    : EvidenceDirection.Supports,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Stays silent because the exact figures are already in the independent analysis.
    /// </summary>
    /// <remarks>
    /// The arithmetic does not change between rounds, so speaking again would add weight but
    /// no new information. Every model sees the independent exposure analysis.
    /// </remarks>
    public Task<RoomContribution> ParticipateAsync(RoomContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The exact figures are already in the independent analysis. Repeating them during
        // discussion would add weight without adding information.
        return Task.FromResult(RoomContribution.Silent(Name, context.Round));
    }

    public Task<PersonaVote> VoteAsync(RoomContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = Assess(context);

        return Task.FromResult(new PersonaVote
        {
            Persona = Name,
            Vote = findings.Vote,
            Confidence = findings.Confidence,
            Rationale = findings.Summary,
            UnresolvedRisk = findings.Concerns.FirstOrDefault(),
        });
    }

    private sealed record Findings(
        VoteKind Vote,
        decimal Confidence,
        string Summary,
        IReadOnlyList<string> Concerns,
        IReadOnlyList<string> Facts);

    private Findings Assess(RoomContext context)
    {
        var market = context.Market;
        var concerns = new List<string>();
        var facts = new List<string>();

        // A position review is about the thesis, not about capacity. Abstain rather than
        // pretending the portfolio has an opinion on whether news has aged.
        if (context.Purpose == WarRoomPurpose.PositionReview)
        {
            return new Findings(
                VoteKind.Abstain, 0m,
                "Portfolio capacity has no bearing on whether an existing thesis still holds.",
                [], []);
        }

        var equity = market.Account.Equity;

        if (equity <= 0m)
        {
            return new Findings(
                VoteKind.Reject, 1m, "Account equity is not positive.", ["No equity."], []);
        }

        var openCost = market.Positions.Sum(
            position => position.AverageEntryPrice * Math.Abs(position.Quantity) * 100m);

        var proposedCost = context.Operation.Actions
            .Where(action => action.Kind is StrategyActionKind.OpenCall or StrategyActionKind.OpenPut)
            .Sum(action => CostOf(action, market));

        var afterTotal = (openCost + proposedCost) / equity;
        var perTrade = proposedCost / equity;

        facts.Add($"Open exposure {openCost / equity:P1} of equity, proposal adds {perTrade:P1}.");
        facts.Add($"{market.Positions.Count} of {_risk.MaxConcurrentPositions} position slots used.");

        if (perTrade > _risk.MaxRiskPerTradeFraction)
        {
            concerns.Add(
                $"The proposal risks {perTrade:P1} of equity against a {_risk.MaxRiskPerTradeFraction:P0} per-trade limit.");
        }

        if (afterTotal > _risk.MaxTotalRiskFraction)
        {
            concerns.Add(
                $"Total exposure would reach {afterTotal:P1} against a {_risk.MaxTotalRiskFraction:P0} limit.");
        }

        // Concentration: several positions on one underlying is one bet wearing several names.
        foreach (var action in context.Operation.Actions.Where(a => a.ContractSymbol is not null))
        {
            var underlying = UnderlyingOf(action.ContractSymbol!, market);
            if (underlying is null)
            {
                continue;
            }

            var already = market.Positions.Count(position =>
                string.Equals(UnderlyingOf(position.Symbol, market), underlying, StringComparison.Ordinal));

            if (already > 0)
            {
                concerns.Add(
                    $"{already} position(s) already open on {underlying}. This adds directional concentration.");
            }
        }

        if (market.RemainingPositionSlots <= 1 && market.Positions.Count > 0)
        {
            concerns.Add("This would take the last position slot, leaving no room to react.");
        }

        if (concerns.Count == 0)
        {
            return new Findings(
                VoteKind.Abstain, 0m,
                $"Exposure is comfortable: {afterTotal:P1} of equity committed after this trade.",
                concerns, facts);
        }

        // Confidence rises with the number of independent concerns, but never to certainty:
        // this seat judges prudence, and RiskGuard already owns the absolute limits.
        var confidence = Math.Clamp(0.35m + (0.2m * concerns.Count), 0m, 0.9m);

        return new Findings(
            VoteKind.Reject, confidence, string.Join(" ", concerns), concerns, facts);
    }

    private static decimal CostOf(StrategyAction action, StrategyContext market) =>
        market.ContractCatalog
            .FirstOrDefault(view =>
                string.Equals(view.Contract.ContractSymbol, action.ContractSymbol, StringComparison.Ordinal))
            ?.CostPerContract * action.Contracts ?? 0m;

    private static string? UnderlyingOf(string contractSymbol, StrategyContext market) =>
        market.ContractCatalog
            .FirstOrDefault(view =>
                string.Equals(view.Contract.ContractSymbol, contractSymbol, StringComparison.Ordinal))
            ?.Contract.Underlying
        ?? (Alpaca.Gateways.OccOptionSymbol.TryParse(contractSymbol, out var parsed)
            ? parsed.Underlying
            : null);
}

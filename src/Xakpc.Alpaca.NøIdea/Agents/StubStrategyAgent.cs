namespace Xakpc.Alpaca.NøIdea.Agents;

/// <summary>
/// A deterministic stand-in for the LLM agent. It spends no tokens.
/// </summary>
/// <remarks>
/// <para>
/// This exists to test the harness, not to trade well. It proves the loop wakes, filters,
/// decides, sizes, submits idempotently, manages positions, and records — everything except
/// whether the reasoning is any good. Running it over stored history is how the plumbing gets
/// checked while the market is closed.
/// </para>
/// <para>
/// Its rule is intentionally simple and carries no claimed edge: take the cheapest candidate
/// whose market probability sits nearest the middle of the policy band, one contract, and only
/// when a position slot is free. <b>Do not read its replay P&amp;L as evidence about the
/// strategy.</b> It is evidence about the code.
/// </para>
/// </remarks>
public sealed class StubStrategyAgent : IStrategyAgent
{
    public string Name => "stub";

    public Task<StrategyDecision> DecideAsync(
        StrategyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.NewPositionsHalted)
        {
            return Task.FromResult(StrategyDecision.Nothing("new positions are halted"));
        }

        if (context.RemainingPositionSlots <= 0)
        {
            return Task.FromResult(StrategyDecision.Nothing("no free position slot"));
        }

        var midBand = (context.Policy.MinMarketProbability + context.Policy.MaxMarketProbability) / 2m;

        var pick = context.Candidates
            .Where(view => view.MarketProbability is not null)
            .OrderBy(view => Math.Abs(view.MarketProbability!.Value - midBand))
            .ThenBy(view => view.CostPerContract)
            .FirstOrDefault();

        if (pick is null)
        {
            return Task.FromResult(StrategyDecision.Nothing("no candidate carried a market probability"));
        }

        var kind = string.Equals(pick.Candidate.OptionType, "put", StringComparison.Ordinal)
            ? StrategyActionKind.OpenPut
            : StrategyActionKind.OpenCall;

        return Task.FromResult(new StrategyDecision
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = kind,
                    ContractSymbol = pick.Candidate.ContractSymbol,
                    Contracts = 1,
                    // The market's own probability, restated. The stub claims no edge, and a
                    // Brier score against this baseline is what a real agent must beat.
                    Probability = pick.MarketProbability,
                    Reasoning =
                        $"stub: nearest the middle of the policy band at {pick.MarketProbability:P1}, "
                        + $"cheapest at {pick.CostPerContract:N2} USD",
                },
            ],
        });
    }
}

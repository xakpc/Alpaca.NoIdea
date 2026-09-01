namespace Xakpc.Alpaca.NøIdea.Agents;

/// <summary>
/// A deterministic stand-in for the LLM agent. It spends no tokens.
/// </summary>
/// <remarks>
/// <para>
/// This exists to test the harness, not to trade well. It proves the loop wakes, filters,
/// decides, sizes, submits idempotently, manages positions, and records — everything except
/// whether the reasoning is any good. A live-data dry run checks the plumbing without orders.
/// </para>
/// <para>
/// Its rule is intentionally simple and carries no claimed edge: take the cheapest tradeable
/// contract, one contract, and only when a position slot is free. <b>Do not read its dry-run result as evidence about the
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
            return Task.FromResult(StrategyDecision.Nothing(
                context.NewPositionsHaltReason ?? "new positions are halted"));
        }

        if (context.RemainingPositionSlots <= 0)
        {
            return Task.FromResult(StrategyDecision.Nothing("no free position slot"));
        }

        var pick = context.ContractCatalog
            .OrderBy(view => view.CostPerContract)
            .ThenBy(view => view.Contract.ContractSymbol, StringComparer.Ordinal)
            .FirstOrDefault();

        if (pick is null)
        {
            return Task.FromResult(StrategyDecision.Nothing("the tradeable catalog is empty"));
        }

        var kind = string.Equals(pick.Contract.OptionType, "put", StringComparison.Ordinal)
            ? StrategyActionKind.OpenPut
            : StrategyActionKind.OpenCall;

        return Task.FromResult(new StrategyDecision
        {
            Actions =
            [
                new StrategyAction
                {
                    Kind = kind,
                    ContractSymbol = pick.Contract.ContractSymbol,
                    Contracts = 1,
                    ProfitProbability = null,
                    Reasoning = $"stub: cheapest mechanically tradeable contract at {pick.CostPerContract:N2} USD",
                },
            ],
        });
    }
}

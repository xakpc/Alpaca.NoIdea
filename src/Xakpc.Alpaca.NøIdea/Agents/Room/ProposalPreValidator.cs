using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>
/// Spec §17. Structural checks on a proposal, run before the room spends a token.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from the cheap filter that builds the candidate list. The
/// filter answers "is this worth an agent call"; this answers "could this trade legally exist
/// at all". Conflating the two hides real faults behind a taste judgement.
/// </para>
/// <para>
/// It is also separate from <c>RiskGuard</c>, which runs again immediately before submission
/// (§24). The market moves during a debate, so a proposal that passed here can still fail
/// there, and that is the intended design rather than a redundancy.
/// </para>
/// <para>
/// Every rejection returns a code, so the audit trail records <em>why</em> a proposal died
/// instead of only that it did.
/// </para>
/// </remarks>
public sealed class ProposalPreValidator(
    RiskOptions risk,
    IReadOnlyList<string> allowedSymbols,
    TimeProvider time)
{
    private readonly RiskOptions _risk = risk ?? throw new ArgumentNullException(nameof(risk));
    private readonly HashSet<string> _allowed =
        new(allowedSymbols ?? throw new ArgumentNullException(nameof(allowedSymbols)), StringComparer.Ordinal);

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Returns a rejection code, or null when the proposal may go to the room.</summary>
    public string? Validate(ProposedOperation operation, WarRoomRequest request)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);

        if (!operation.TradesAnything)
        {
            return null;   // NO_TRADE and HOLD are valid answers, not faults.
        }

        var market = request.Market;
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

        foreach (var action in operation.Actions.Where(a => a.Kind != StrategyActionKind.Hold))
        {
            if (!request.AllowedActions.Contains(action.Kind))
            {
                return "REJECT_INVALID_STRATEGY";
            }

            if (string.IsNullOrWhiteSpace(action.ContractSymbol))
            {
                return "REJECT_INVALID_CONTRACT";
            }

            if (action.Contracts < 1)
            {
                return "REJECT_INVALID_QUANTITY";
            }

            // Closing is judged against the open positions, not the candidate list.
            if (action.Kind == StrategyActionKind.ClosePosition)
            {
                if (market.Positions.All(position =>
                        !string.Equals(position.Symbol, action.ContractSymbol, StringComparison.Ordinal)))
                {
                    return "REJECT_NO_SUCH_POSITION";
                }

                continue;
            }

            var view = market.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Candidate.ContractSymbol, action.ContractSymbol, StringComparison.Ordinal));

            if (view is null)
            {
                return "REJECT_INVALID_CONTRACT";
            }

            var contract = view.Candidate;

            if (!_allowed.Contains(contract.Underlying))
            {
                return "REJECT_INVALID_SYMBOL";
            }

            // The proposed direction must match the contract. A call cannot express a put.
            var wanted = action.Kind == StrategyActionKind.OpenCall ? "call" : "put";
            if (!string.Equals(contract.OptionType, wanted, StringComparison.Ordinal))
            {
                return "REJECT_INVALID_CONTRACT";
            }

            var daysToExpiration = contract.Expiration.DayNumber - today.DayNumber;

            if (daysToExpiration < _risk.HardMinDaysToExpiration
                || daysToExpiration > _risk.HardMaxDaysToExpiration)
            {
                return "REJECT_EXPIRATION";
            }

            // §24.4 in advance: a contract that outlives the measurement point cannot be
            // managed to a conclusion, so it is not worth debating.
            if (contract.Expiration.ToDateTime(TimeOnly.MinValue)
                > _risk.CompetitionFlattenUtc.UtcDateTime.Date.AddDays(1))
            {
                return "REJECT_EXPIRATION";
            }

            // Replay carries no quote at all, and says so. A live candidate must be tradeable.
            if (contract.Quality != QuoteQuality.UnknownHistorical)
            {
                if (!contract.IsTradeableQuote)
                {
                    return "REJECT_BAD_QUOTE";
                }

                if (contract.Ask is not { } ask || ask <= 0m || contract.Spread is not { } spread)
                {
                    return "REJECT_BAD_QUOTE";
                }

                if (spread / ask > _risk.MaxSpreadFraction)
                {
                    return "REJECT_LIQUIDITY";
                }

                if (contract.QuoteTimestampUtc is not { } quotedAt
                    || _time.GetUtcNow() - quotedAt > _risk.MaxQuoteAge)
                {
                    return "REJECT_BAD_QUOTE";
                }
            }

            if (view.CostPerContract <= 0m)
            {
                return "REJECT_BAD_QUOTE";
            }

            // §17.8: never open a second position in a contract already held.
            if (market.Positions.Any(position =>
                    string.Equals(position.Symbol, action.ContractSymbol, StringComparison.Ordinal)))
            {
                return "REJECT_DUPLICATE";
            }

            // §17.9: reject before debate when no legal size could exist.
            if (market.Account.Equity <= 0m)
            {
                return "REJECT_NO_CAPACITY";
            }

            if (view.CostPerContract > market.Account.Equity * _risk.MaxRiskPerTradeFraction)
            {
                // Even a single contract breaks the per-trade cap, so no quantity is legal.
                return "REJECT_RISK_TOO_LARGE";
            }
        }

        if (market.RemainingPositionSlots <= 0)
        {
            return "REJECT_NO_POSITION_SLOT";
        }

        return null;
    }
}

using System.Globalization;

namespace Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

/// <summary>
/// Parses an OCC option contract symbol into its parts.
/// </summary>
/// <remarks>
/// <para>
/// The format is a variable-length root, then a six-digit expiration, then C or P, then an
/// eight-digit strike in thousandths:
/// </para>
/// <code>
/// AAPL  240119  C  00180000   ->  AAPL, 2024-01-19, call, strike 180.000
/// </code>
/// <para>
/// The parse runs from the right, because the root is the only variable-width field. The
/// strike divides by 1000 in <see cref="decimal"/>, never in a binary float: a half-dollar
/// strike is exact in decimal and is not in double.
/// </para>
/// <para>
/// Alpaca returns the contract symbol as a chain key with no parsed parts. This parser gives
/// the live loop and the durable audit one interpretation of strike and expiration.
/// </para>
/// </remarks>
public readonly record struct OccOptionSymbol(
    string ContractSymbol,
    string Underlying,
    DateOnly Expiration,
    bool IsCall,
    decimal Strike)
{
    /// <summary>The fixed-width tail: 6 date digits, 1 type letter, 8 strike digits.</summary>
    private const int TailLength = 15;

    public string OptionType => IsCall ? "call" : "put";

    public static bool TryParse(string? contractSymbol, out OccOptionSymbol parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(contractSymbol) || contractSymbol.Length <= TailLength)
        {
            return false;
        }

        var tail = contractSymbol.Length - TailLength;
        var underlying = contractSymbol[..tail];
        var datePart = contractSymbol.Substring(tail, 6);
        var typePart = contractSymbol[tail + 6];
        var strikePart = contractSymbol[(tail + 7)..];

        if (!DateOnly.TryParseExact(
                datePart, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiration))
        {
            return false;
        }

        var isCall = typePart is 'C' or 'c';
        if (!isCall && typePart is not ('P' or 'p'))
        {
            return false;
        }

        if (!long.TryParse(
                strikePart, NumberStyles.None, CultureInfo.InvariantCulture, out var strikeThousandths))
        {
            return false;
        }

        parsed = new OccOptionSymbol(
            contractSymbol, underlying, expiration, isCall, strikeThousandths / 1000m);

        return true;
    }
}

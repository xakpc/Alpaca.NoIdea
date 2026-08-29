using Alpaca.Markets;

namespace Xakpc.Alpaca.NøIdea.Alpaca;

/// <summary>
/// The typed Alpaca REST clients. Deterministic C# uses these, and only these, to
/// read the account and to move money. The LLM agents never receive them.
/// </summary>
/// <remarks>
/// <para>
/// The environment is <see cref="Environments.Paper"/> and it is not configurable.
/// Reaching live trading requires editing this file, which is a stronger guarantee
/// than a runtime account check: there is no configuration value, environment
/// variable, or argument that can move this process to a live account.
/// </para>
/// <para>
/// No market-data feed is named anywhere (ADR-010). The account default applies.
/// </para>
/// </remarks>
public sealed class AlpacaClients : IDisposable
{
    /// <summary>The only environment this application supports.</summary>
    public static IEnvironment Environment => Environments.Paper;

    public AlpacaClients(AlpacaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = new SecretKey(options.ApiKey, options.SecretKey);

        Trading = Environment.GetAlpacaTradingClient(key);
        StockData = Environment.GetAlpacaDataClient(key);
        OptionsData = Environment.GetAlpacaOptionsDataClient(key);
    }

    /// <summary>Account, positions, orders. The only write path in the system.</summary>
    public IAlpacaTradingClient Trading { get; }

    /// <summary>Stock bars, quotes, and snapshots.</summary>
    public IAlpacaDataClient StockData { get; }

    /// <summary>Option chains, snapshots, quotes, and greeks.</summary>
    public IAlpacaOptionsDataClient OptionsData { get; }

    public void Dispose()
    {
        Trading.Dispose();
        StockData.Dispose();
        OptionsData.Dispose();
    }
}

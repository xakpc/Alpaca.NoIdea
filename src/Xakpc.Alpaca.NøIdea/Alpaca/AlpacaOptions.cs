namespace Xakpc.Alpaca.NøIdea.Alpaca;

/// <summary>
/// The Alpaca credentials. They come from the environment, never from disk in the
/// deployed image. For local development they can come from the repository-root
/// <c>.env</c> file, which is git-ignored.
/// </summary>
public sealed record AlpacaOptions(string ApiKey, string SecretKey)
{
    /// <summary>
    /// Reads the credentials from environment variables, and falls back to the
    /// <c>.env</c> file when a variable is not set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A required key is missing, or <c>ALPACA_PAPER_TRADE</c> is not <c>true</c>.
    /// </exception>
    public static AlpacaOptions FromEnvironment(string? envFilePath = null)
    {
        var fromFile = ReadEnvFile(envFilePath);

        string Get(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? (fromFile.TryGetValue(name, out var value) ? value : null)
            ?? throw new InvalidOperationException(
                $"{name} is not set. Copy .env.example to .env and fill in the paper account keys.");

        // The MCP server reads this variable. The SDK does not, because the paper
        // guarantee there is Environments.Paper at compile time. Assert they agree,
        // so one half of the system cannot be pointed at live trading alone.
        var paper = Environment.GetEnvironmentVariable("ALPACA_PAPER_TRADE")
                    ?? (fromFile.TryGetValue("ALPACA_PAPER_TRADE", out var p) ? p : "true");

        if (!string.Equals(paper.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"ALPACA_PAPER_TRADE is '{paper}'. This project trades paper accounts only.");
        }

        return new AlpacaOptions(Get("ALPACA_API_KEY"), Get("ALPACA_SECRET_KEY"));
    }

    /// <summary>
    /// One value from the environment, falling back to the git-ignored <c>.env</c> file.
    /// </summary>
    /// <remarks>
    /// Secrets belong in <c>.env</c>, never in <c>launchSettings.json</c>, which is tracked.
    /// </remarks>
    public static string? Secret(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : ReadEnvFile(null).GetValueOrDefault(name);

    private static Dictionary<string, string> ReadEnvFile(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        path ??= FindRepositoryFile(".env");
        if (path is null || !File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var separator = text.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[text[..separator].Trim()] = text[(separator + 1)..].Trim();
        }

        return result;
    }

    private static string? FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

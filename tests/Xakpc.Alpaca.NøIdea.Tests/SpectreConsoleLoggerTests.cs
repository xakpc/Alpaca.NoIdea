using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Xakpc.Alpaca.NøIdea.Observability;

namespace Xakpc.Alpaca.NøIdea.Tests;

public class SpectreConsoleLoggerTests
{
    [Fact]
    public void KnownRunEventsRenderAnOperatorView()
    {
        var (console, output) = CaptureConsole();
        using var provider = new SpectreConsoleLoggerProvider(false, console);
        var logger = provider.CreateLogger("Trader");

        logger.LogInformation(
            RunEvents.RunStarted,
            "Run started. Mode {Mode}. Dry run {DryRun}. Seats: {Seats}.",
            "dry-run", true, "proposer, quant");
        logger.LogInformation(
            RunEvents.AccountRead,
            "Equity {Equity:N2} USD, cash {Cash:N2}, {Positions} position(s).",
            100_000m, 91_250m, 2);
        logger.LogInformation(
            RunEvents.CycleFinished,
            "Cycle {Number}: {Offered} candidates, {Opened} open order(s), "
            + "{CloseSubmitted} close order(s), {Closed} confirmed closed, "
            + "{Rejected} rejected, equity {Equity:N2} USD. Run cost {Cost}.",
            3, 1461, 1, 0, 0, 2, 100_125.50m, "8 calls, about 1.20 USD");

        var text = Normalize(output.ToString());

        Assert.Contains("AUTONOMOUS OPTIONS RUN", text, StringComparison.Ordinal);
        Assert.Contains("DRY RUN", text, StringComparison.Ordinal);
        Assert.Contains("account", text, StringComparison.Ordinal);
        Assert.Contains("equity 100,000.00 USD", text, StringComparison.Ordinal);
        Assert.Contains("CYCLE SUMMARY", text, StringComparison.Ordinal);
        Assert.Contains("1,461", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trader[", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void CatalogBreakdownRendersStructuredRowsAndKeepsItsPlainText()
    {
        var (console, output) = CaptureConsole();
        using var provider = new SpectreConsoleLoggerProvider(false, console);
        var logger = provider.CreateLogger("Trader");
        var reasons = new EventCountBreakdown(new Dictionary<string, int>
        {
            ["spread-too-wide"] = 11,
            ["quote-too-old"] = 25,
        });

        logger.LogInformation(
            RunEvents.CatalogFiltered,
            "Dropped {Dropped} of {Examined} contract(s): {Reasons}.",
            36, 40, reasons);

        var text = output.ToString();

        Assert.Contains("removed 36 contract(s)", text, StringComparison.Ordinal);
        Assert.Contains("Quote too old", text, StringComparison.Ordinal);
        Assert.Contains("Spread too wide", text, StringComparison.Ordinal);
        Assert.Equal("quote-too-old 25, spread-too-wide 11", reasons.ToString());
    }

    [Fact]
    public void CuratedConsoleHidesTranscriptButPlainFileKeepsIt()
    {
        var (console, output) = CaptureConsole();
        var path = Path.Combine(Path.GetTempPath(), $"noidea-log-{Guid.NewGuid():N}.txt");

        try
        {
            string consoleText;
            {
                using var fileProvider = new PlainFileLoggerProvider(path);
                using var consoleProvider = new SpectreConsoleLoggerProvider(false, console);
                using var factory = LoggerFactory.Create(builder =>
                   {
                       builder.SetMinimumLevel(LogLevel.Information);
                       builder.AddProvider(fileProvider);
                       builder.AddProvider(consoleProvider);
                   });
                var logger = factory.CreateLogger("Trader");
                logger.LogInformation(
                    RunEvents.Chat("quant", ChatEvent.Sending),
                    "{Persona} [{Phase}] asks {Model}. Sending {Messages} messages, "
                    + "{Characters} characters, {Tools} tools. Sent at {SentUtc}.",
                    "quant", "analysis", "gpt-test", 2, 1234, 3,
                    new DateTimeOffset(2026, 9, 1, 5, 30, 0, TimeSpan.Zero));
                logger.LogInformation(
                    RunEvents.Chat("quant", ChatEvent.Request),
                    "{Persona} [{Phase}] sent user: {Text}",
                    "quant", "analysis", "SECRET PROMPT CONTENT");
                logger.LogInformation(
                    RunEvents.Chat("quant", ChatEvent.ToolResult),
                    "{Persona} [{Phase}] tool answered: {Result}",
                    "quant", "analysis", "SECRET TOOL RESULT");
                logger.LogInformation(
                    RunEvents.Chat("quant", ChatEvent.Finished),
                    "{Persona} [{Phase}] finished on {Model}: {Reason}, {Turns} turns, "
                    + "{ToolCalls} tool calls [{Tools}], {Input} in / {Output} out, {Seconds}s.",
                    "quant", "analysis", "gpt-test", "stop", 2, 3, "bars, news",
                    15_000, 700, 12.5);

                consoleText = Normalize(output.ToString());
            }

            var fileText = File.ReadAllText(path);

            Assert.Contains("quant", consoleText, StringComparison.Ordinal);
            Assert.Contains("12.5s", consoleText, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET PROMPT CONTENT", consoleText, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET TOOL RESULT", consoleText, StringComparison.Ordinal);
            Assert.Contains("SECRET PROMPT CONTENT", fileText, StringComparison.Ordinal);
            Assert.Contains("SECRET TOOL RESULT", fileText, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ConcurrentModelCallsOwnIndependentLiveRows()
    {
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true);

        renderer.Process(Sending("quant", "analysis", 1));
        renderer.Process(Sending("skeptic", "analysis", 2));

        Assert.Equal(2, renderer.ActiveCallCount);
        console.Write(renderer.BuildLiveDisplay());
        Assert.Contains("quant", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("skeptic", output.ToString(), StringComparison.Ordinal);

        renderer.Process(Finished("quant", "analysis"));

        Assert.Equal(1, renderer.ActiveCallCount);
    }

    [Fact]
    public void ModelAndProposalTextCannotInjectSpectreMarkup()
    {
        var (console, output) = CaptureConsole();
        using var provider = new SpectreConsoleLoggerProvider(false, console);
        var logger = provider.CreateLogger("Trader");

        logger.LogInformation(
            RunEvents.ProposalMade,
            "{Id}: {Count} action(s). {Thesis}",
            "proposal-[red]owned[/]", 1, "Buy [bold red]everything[/].");

        var text = output.ToString();

        Assert.Contains("proposal-[red]owned[/]", text, StringComparison.Ordinal);
        Assert.Contains("[bold red]everything[/]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptWarningsAreNeverHidden()
    {
        var (console, output) = CaptureConsole();
        using var provider = new SpectreConsoleLoggerProvider(false, console);
        var logger = provider.CreateLogger("Trader");

        logger.LogWarning(
            RunEvents.Chat("market", ChatEvent.Said),
            "{Persona} [{Phase}] returned an error block: {Message}",
            "market", "vote", "Provider refused the response.");

        Assert.Contains(
            "Provider refused the response",
            Normalize(output.ToString()),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The regression test for the operator view that beeped. A console on an OEM code page maps
    /// '•' to 0x07 with its best-fit table, and model prose can carry an escape or a newline of
    /// its own. Either one reaches the terminal as a command, not as text.
    /// </remarks>
    [Fact]
    public void NoControlCharacterEverReachesTheConsole()
    {
        var (console, output) = CaptureConsole();
        using var provider = new SpectreConsoleLoggerProvider(false, console);

        LogOneOfEachEvent(provider.CreateLogger("Trader"), Hostile);

        var offenders = output.ToString()
            .Where(character => character is not ('\n' or '\r')
                                && (character < ' ' || character == '\u007F'))
            .Select(character => $"U+{(int)character:X4}")
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    /// <remarks>
    /// The second guard. If the process cannot switch the console to UTF-8, every symbol the view
    /// draws must already be a character the console can encode.
    /// </remarks>
    [Fact]
    public void AConsoleWithoutUnicodeGetsAnAsciiView()
    {
        var (console, output) = CaptureConsole();
        console.Profile.Capabilities.Unicode = false;

        using var provider = new SpectreConsoleLoggerProvider(false, console);

        LogOneOfEachEvent(provider.CreateLogger("Trader"), "plain ascii text");

        var offenders = output.ToString()
            .Where(character => character > '~')
            .Select(character => $"U+{(int)character:X4}")
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
        Assert.Same(ConsoleGlyphs.Ascii, ConsoleGlyphs.For(console));
    }

    /// <remarks>
    /// Spectre restores the cursor over the region it drew last, so a line written straight to the
    /// console between two refreshes is erased by the next one. In live mode the renderer must put
    /// every line in the target instead.
    /// </remarks>
    [Fact]
    public void TheLiveDisplayHoldsEveryLineInsteadOfWritingAroundItself()
    {
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true);

        renderer.Process(Generic("first line of the run"));
        renderer.Process(Sending("quant", "analysis", 1));
        renderer.Process(Generic("second line of the run"));

        // Nothing may reach the console on its own while the display owns it.
        Assert.Equal(string.Empty, output.ToString());

        console.Write(renderer.BuildLiveDisplay());
        var live = Normalize(output.ToString());

        Assert.Contains("first line of the run", live, StringComparison.Ordinal);
        Assert.Contains("second line of the run", live, StringComparison.Ordinal);
        Assert.Contains("quant", live, StringComparison.Ordinal);
    }

    [Fact]
    public void LosingTheLiveDisplayWritesOutEverythingItStillHeld()
    {
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true);

        renderer.Process(Generic("held while the display was up"));
        renderer.DisableLiveDisplay(new InvalidOperationException("terminal went away"));

        var text = Normalize(output.ToString());

        Assert.Contains("held while the display was up", text, StringComparison.Ordinal);
        Assert.Contains("terminal went away", text, StringComparison.Ordinal);

        renderer.Process(Generic("after the display was lost"));

        Assert.Contains(
            "after the display was lost",
            Normalize(output.ToString()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SafeTextDropsControlCharactersAndClipsWholeCharacters()
    {
        Assert.Equal("bell [0m red", ConsoleText.Safe("\u0007bell \u001B[0m\tred\r\n"));
        Assert.Equal("one two", ConsoleText.Safe("one     \n\n   two"));

        // U+1F600 is one scalar value held in two chars. A clip must not keep half of it.
        var clipped = ConsoleText.Safe("ab\U0001F600cd", 3, "|");

        Assert.Equal("ab\U0001F600|", clipped);
        Assert.True(clipped.EnumerateRunes().All(rune => rune.Value != 0xFFFD));
        Assert.Equal("ab", ConsoleText.Safe("abcd", 2, string.Empty));
        Assert.Equal(string.Empty, ConsoleText.Safe(null));
    }

    [Fact]
    public void AWaitingSeatShowsHowLongItHasBeenWaiting()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 9, 1, 5, 30, 0, TimeSpan.Zero));
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true, timeProvider: clock);

        renderer.Process(Sending("quant", "analysis", 0));
        clock.Advance(TimeSpan.FromSeconds(83));
        console.Write(renderer.BuildLiveDisplay());

        Assert.Contains("1:23", Normalize(output.ToString()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpinnerTurnsOnlyWhenTheLoopAdvancesIt()
    {
        var (console, _) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true);

        renderer.Process(Sending("quant", "analysis", 0));

        // Building the picture must not move the spinner, or a test could never pin a frame.
        var first = Render(console, renderer.BuildLiveDisplay());
        Assert.Equal(first, Render(console, renderer.BuildLiveDisplay()));

        renderer.AdvanceSpinner();

        Assert.NotEqual(first, Render(console, renderer.BuildLiveDisplay()));
    }

    [Fact]
    public void EveryAsciiSpinnerFrameStaysAscii()
    {
        Assert.False(ConsoleGlyphs.Ascii.Spinner.IsUnicode);
        Assert.True(ConsoleGlyphs.Unicode.Spinner.IsUnicode);

        for (var tick = 0; tick < 24; tick++)
        {
            Assert.All(ConsoleGlyphs.Ascii.Frame(tick), character => Assert.True(character <= '~'));
        }
    }

    /// <remarks>
    /// The session waits half an hour between cycles. Without this the view holds one still
    /// picture for that long, and an operator cannot tell a live run from a hung one.
    /// </remarks>
    [Fact]
    public void TheIdleSessionCountsDownAndACycleStartClearsIt()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 9, 1, 5, 30, 0, TimeSpan.Zero));
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: true, timeProvider: clock);

        Assert.False(renderer.HasLiveWork);

        renderer.Process(Waiting(clock.GetUtcNow().AddMinutes(30)));

        Assert.True(renderer.HasLiveWork);

        clock.Advance(TimeSpan.FromMinutes(2));
        console.Write(renderer.BuildLiveDisplay());
        var waiting = Normalize(output.ToString());

        Assert.Contains("28:00 left", waiting, StringComparison.Ordinal);
        Assert.Contains("resumes 06:00:00 UTC", waiting, StringComparison.Ordinal);

        renderer.Process(CycleStarted());

        Assert.False(renderer.HasLiveWork);
    }

    [Fact]
    public void TheStaticViewStatesTheWaitOnceAndNeverSpins()
    {
        var (console, output) = CaptureConsole();
        var renderer = new SpectreLogRenderer(console, liveDisplay: false);

        renderer.Process(Waiting(new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero)));

        var text = Normalize(output.ToString());

        Assert.Contains("waiting", text, StringComparison.Ordinal);
        Assert.Contains("30 minute(s) until the next cycle", text, StringComparison.Ordinal);
        Assert.Contains("resumes 06:00:00 UTC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("left", text, StringComparison.Ordinal);

        // Nothing on the static path animates, so it must not draw a spinner frame either.
        Assert.All(
            ConsoleGlyphs.Unicode.Spinner.Frames,
            frame => Assert.DoesNotContain(frame, text, StringComparison.Ordinal));
    }

    private const string Hostile =
        "\u0007bell \u001B[31mred\u001B[0m \u0000null \u202Ebidi\r\nsecond line\u007F";

    private static void LogOneOfEachEvent(ILogger logger, string text)
    {
        logger.LogInformation(
            RunEvents.RunStarted, "Run started. {Mode} {DryRun} {Seats}", text, true, text);
        logger.LogInformation(RunEvents.RunStopped, "Stopped. {Cycles} {Cost}", 3, text);
        logger.LogInformation(
            RunEvents.RoomSpend, "{Persona} {Model} {Calls} {Tokens} {Usd}", text, text, 8, 900L, text);
        logger.LogInformation(
            RunEvents.CycleStarted, "Cycle {Number} {At} {Open}", 3, DateTimeOffset.UnixEpoch, true);
        logger.LogInformation(
            RunEvents.CycleFinished,
            "Cycle {Number} {Offered} {Opened} {CloseSubmitted} {Closed} {Rejected} {Equity} {Cost}",
            3, 1461, 1, 0, 0, 2, 100_125.50m, text);
        logger.LogInformation(
            RunEvents.CycleWaiting, "Waiting {Minutes} {Reason}. Next at {ResumeUtc}.",
            30d, text, DateTimeOffset.UnixEpoch);
        logger.LogInformation(
            RunEvents.AccountRead, "{Equity} {Cash} {Positions}", 100_000m, 91_250m, 2);
        logger.LogInformation(RunEvents.CandidatesBuilt, "{Offered} {Scanned}", 1461, 13);
        logger.LogInformation(RunEvents.Hold, "Holding because {Reason}", text);
        logger.LogInformation(RunEvents.OrderDecided, "Buying because {Why}", text);
        logger.LogInformation(RunEvents.PositionClosed, "Closing because {Why}", text);
        logger.LogWarning(RunEvents.RiskRejected, "Rejected because {Why}", text);
        logger.LogInformation(
            RunEvents.CatalogFiltered,
            "Dropped {Dropped} of {Examined} contract(s): {Reasons}.",
            36,
            40,
            new EventCountBreakdown(new Dictionary<string, int> { ["quote-too-old"] = 36 }));
        logger.LogInformation(RunEvents.ProposalMade, "{Id} {Count} {Thesis}", text, 2, text);
        logger.LogInformation(RunEvents.ProposalRejectedEarly, "Refused: {Why}", text);
        logger.LogInformation(
            RunEvents.AnalysisReceived,
            "{Persona} {Vote} {Confidence} {Analysis}", text, "APPROVE", 0.8m, text);
        logger.LogInformation(
            RunEvents.DiscussionHeard, "{Round} {Speaker} {Summary}", 2, text, text);
        logger.LogInformation(RunEvents.RebuttalMade, "Rebuttal: {Text}", text);
        logger.LogInformation(
            RunEvents.VoteCast,
            "{Persona} {Vote} {Confidence} {Rationale}", text, "REJECT", 0.4m, text);
        logger.LogInformation(
            RunEvents.VerdictReached, "{Verdict} {Id} {Tally} {Cost}", "APPROVED", text, text, text);
        logger.LogInformation(
            RunEvents.Chat("quant", ChatEvent.Sending),
            "{Persona} {Phase} {Model} {Messages} {Characters} {Tools} {SentUtc}",
            text, text, text, 2, 1234, 3, DateTimeOffset.UnixEpoch);
        logger.LogInformation(
            RunEvents.Chat("quant", ChatEvent.Finished),
            "{Persona} {Phase} {Seconds} {Input} {Output} {ToolCalls} {Tools}",
            text, text, 12.5, 1000L, 100L, 1, text);
        logger.LogWarning(
            RunEvents.Chat("market", ChatEvent.Said), "Error block: {Message}", text);
        logger.LogError(new InvalidOperationException(text), "Unnumbered failure: {Detail}", text);
    }

    private static SpectreLogEntry Waiting(DateTimeOffset resumeAt) => new(
        new DateTimeOffset(2026, 9, 1, 5, 30, 0, TimeSpan.Zero),
        "Trader",
        LogLevel.Information,
        RunEvents.CycleWaiting,
        "waiting",
        null,
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Minutes"] = 30d,
            ["Reason"] = "until the next cycle",
            ["ResumeUtc"] = resumeAt,
        });

    private static SpectreLogEntry CycleStarted() => new(
        new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero),
        "Trader",
        LogLevel.Information,
        RunEvents.CycleStarted,
        "cycle",
        null,
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Number"] = 4,
            ["At"] = new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero),
            ["Open"] = true,
        });

    private static string Render(IAnsiConsole console, Spectre.Console.Rendering.IRenderable value)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var target = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output),
        });
        target.Profile.Width = console.Profile.Width;
        target.Write(value);

        return output.ToString();
    }

    private static SpectreLogEntry Generic(string message) => new(
        new DateTimeOffset(2026, 9, 1, 5, 30, 0, TimeSpan.Zero),
        "Trader",
        LogLevel.Information,
        new EventId(0),
        message,
        null,
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    private static SpectreLogEntry Sending(string persona, string phase, int second) => new(
        new DateTimeOffset(2026, 9, 1, 5, 30, second, TimeSpan.Zero),
        "Trader",
        LogLevel.Information,
        RunEvents.Chat(persona, ChatEvent.Sending),
        "sending",
        null,
        new Dictionary<string, object?>
        {
            ["Persona"] = persona,
            ["Phase"] = phase,
            ["Model"] = $"{persona}-model",
            ["Messages"] = 2,
            ["Characters"] = 1000,
            ["Tools"] = 3,
            ["SentUtc"] = new DateTimeOffset(2026, 9, 1, 5, 30, second, TimeSpan.Zero),
        });

    private static SpectreLogEntry Finished(string persona, string phase) => new(
        new DateTimeOffset(2026, 9, 1, 5, 30, 20, TimeSpan.Zero),
        "Trader",
        LogLevel.Information,
        RunEvents.Chat(persona, ChatEvent.Finished),
        "finished",
        null,
        new Dictionary<string, object?>
        {
            ["Persona"] = persona,
            ["Phase"] = phase,
            ["Seconds"] = 19d,
            ["Input"] = 1000L,
            ["Output"] = 100L,
            ["ToolCalls"] = 1,
            ["Tools"] = "vote",
        });

    private static (IAnsiConsole Console, StringWriter Output) CaptureConsole()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output),
        });

        return (console, output);
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ");
}

/// <summary>A <see cref="TimeProvider"/> a test can move forward.</summary>
/// <remarks>
/// <see cref="FakeClock"/> is pinned to one instant, and the risk tests depend on that. An
/// elapsed time and a countdown both need the clock to move.
/// </remarks>
internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

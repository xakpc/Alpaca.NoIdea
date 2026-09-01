using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>Renders the operator-facing application log with Spectre.Console.</summary>
public sealed class SpectreConsoleLoggerProvider : ILoggerProvider
{
    /// <summary>How often the live view repaints while a call or a wait is running.</summary>
    /// <remarks>
    /// Four frames a second reads as motion and costs a quarter of what Spectre's own progress
    /// display spends. The whole live region is redrawn each time, so a faster rate buys very
    /// little and can flicker over a slow connection.
    /// </remarks>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly Channel<SpectreLogEntry>? _entries;
    private readonly SpectreLogRenderer _renderer;
    private readonly Task? _liveTask;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public SpectreConsoleLoggerProvider(
        bool liveMode,
        IAnsiConsole? console = null,
        TimeProvider? timeProvider = null)
    {
        var output = console ?? AnsiConsole.Console;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var useLiveDisplay = liveMode
                             && output.Profile.Capabilities.Ansi
                             && output.Profile.Capabilities.Interactive;

        _renderer = new SpectreLogRenderer(output, useLiveDisplay, timeProvider: _timeProvider);

        if (useLiveDisplay)
        {
            _entries = Channel.CreateUnbounded<SpectreLogEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            _liveTask = Task.Run(() => RunLiveDisplayAsync(output, _entries.Reader));
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        new SpectreConsoleLogger(this, categoryName);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_entries is not null)
        {
            _entries.Writer.TryComplete();
        }

        if (_liveTask is not null)
        {
            try
            {
                _liveTask.GetAwaiter().GetResult();
            }
            catch
            {
                // Console presentation must not mask the application's result.
            }
        }
    }

    internal void Write(SpectreLogEntry entry)
    {
        if (_disposed)
        {
            return;
        }

        if (_entries is not null)
        {
            _entries.Writer.TryWrite(entry);
            return;
        }

        _renderer.Process(entry);
    }

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    /// <remarks>
    /// <b>Nothing writes to the console while this display owns it.</b> Spectre restores the
    /// cursor over the region it drew last, so a direct write between two refreshes is erased by
    /// the next one. The renderer buffers each finished line instead, and this loop hands the
    /// whole picture - the log tail and the seats that are waiting - to the live target as one
    /// renderable.
    /// </remarks>
    private async Task RunLiveDisplayAsync(
        IAnsiConsole console,
        ChannelReader<SpectreLogEntry> reader)
    {
        try
        {
            await console.Live(_renderer.BuildLiveDisplay())
                // The last frame is the record of how the run ended, so it stays on screen.
                .AutoClear(false)
                // The tail is longer than the window. Keep the newest lines and the seat table.
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(context => RefreshAsync(context, reader)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _renderer.DisableLiveDisplay(error);

            await foreach (var entry in reader.ReadAllAsync().ConfigureAwait(false))
            {
                _renderer.Process(entry);
            }
        }
    }

    /// <summary>Draws on a new log entry, and on a timer while something is still running.</summary>
    /// <remarks>
    /// <para>
    /// A model call takes minutes and the session waits half an hour between cycles. Without a
    /// timer the view holds one still picture for all of that, and a live run cannot be told
    /// apart from a hung one. The timer is what makes the spinner turn and the clocks count.
    /// </para>
    /// <para>
    /// <b>Both waits are held outside the loop on purpose.</b> A
    /// <see cref="PeriodicTimer"/> allows one outstanding <c>WaitForNextTickAsync</c>. Asking
    /// again while an abandoned one is still pending throws.
    /// </para>
    /// </remarks>
    private async Task RefreshAsync(
        LiveDisplayContext context,
        ChannelReader<SpectreLogEntry> reader)
    {
        using var ticker = new PeriodicTimer(RefreshInterval, _timeProvider);
        var pending = reader.WaitToReadAsync().AsTask();
        var tick = ticker.WaitForNextTickAsync().AsTask();

        while (true)
        {
            var completed = await Task.WhenAny(pending, tick).ConfigureAwait(false);

            if (completed == pending)
            {
                if (!await pending.ConfigureAwait(false))
                {
                    break;
                }

                while (reader.TryRead(out var entry))
                {
                    _renderer.Process(entry);
                }

                pending = reader.WaitToReadAsync().AsTask();
            }
            else
            {
                if (!await tick.ConfigureAwait(false))
                {
                    break;
                }

                tick = ticker.WaitForNextTickAsync().AsTask();

                // An idle wake-up costs a timer callback. Only the drawing is worth avoiding.
                if (!_renderer.HasLiveWork)
                {
                    continue;
                }

                _renderer.AdvanceSpinner();
            }

            context.UpdateTarget(_renderer.BuildLiveDisplay());
        }
    }

    private sealed class SpectreConsoleLogger(
        SpectreConsoleLoggerProvider provider,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!IsEnabled(logLevel))
            {
                return;
            }

            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var pair in values)
                {
                    if (!string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    {
                        properties[pair.Key] = pair.Value;
                    }
                }
            }

            provider.Write(new SpectreLogEntry(
                provider.GetUtcNow(),
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                properties));
        }
    }
}

internal sealed record SpectreLogEntry(
    DateTimeOffset At,
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties)
{
    public object? Value(string name) => Properties.GetValueOrDefault(name);

    public string Text(string name, string fallback = "-") =>
        Value(name)?.ToString() ?? fallback;

    public int Integer(string name) => Convert.ToInt32(
        Value(name) ?? 0,
        CultureInfo.InvariantCulture);

    public long Long(string name) => Convert.ToInt64(
        Value(name) ?? 0L,
        CultureInfo.InvariantCulture);

    public decimal Decimal(string name) => Convert.ToDecimal(
        Value(name) ?? 0m,
        CultureInfo.InvariantCulture);

    public double Double(string name) => Convert.ToDouble(
        Value(name) ?? 0d,
        CultureInfo.InvariantCulture);

    public bool Boolean(string name) => Convert.ToBoolean(
        Value(name) ?? false,
        CultureInfo.InvariantCulture);

    public DateTimeOffset Timestamp(string name) => Value(name) switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new DateTimeOffset(timestamp),
        _ => At,
    };
}

/// <summary>Turns one log entry into the operator view for it.</summary>
/// <remarks>
/// <para>
/// <b>The layout is a gutter, not a sentence.</b> An ordinary event draws
/// <c>time, symbol, label, message</c> in fixed columns, so the operator reads down one column
/// instead of reading each line. A panel is kept for the four moments worth a stop: the run
/// starts, the run ends, a cycle closes, and the room reaches a verdict.
/// </para>
/// <para>
/// <b>Colour marks the kind of event, never the prose.</b> The symbol and the label carry the
/// style and the message text stays plain, because a wall of coloured sentences hides the one
/// line that matters.
/// </para>
/// <para>
/// <b>Every symbol comes from <see cref="ConsoleGlyphs"/> and every untrusted string goes
/// through <see cref="ConsoleText.Safe"/>.</b> A literal here would beep or move the cursor on a
/// console that does not hold the character.
/// </para>
/// </remarks>
internal sealed class SpectreLogRenderer
{
    private const int TimeWidth = 8;
    private const int SymbolWidth = 1;
    private const int LabelWidth = 9;

    /// <summary>Finished lines the live display keeps. Spectre crops what the window cannot hold.</summary>
    private const int MaxBufferedLines = 200;

    private const int ThesisLimit = 400;
    private const int AnalysisLimit = 320;
    private const int DiscussionLimit = 280;
    private const int RationaleLimit = 260;
    private const int MessageLimit = 1_000;
    private const int ReasonWidth = 28;
    private const int CountWidth = 7;

    /// <summary>Invariant culture writes "62 %". The gutter has no room for the space.</summary>
    private static readonly NumberFormatInfo Percent = CreatePercentFormat();

    private static readonly Style Accent = new(Color.Aqua, decoration: Decoration.Bold);
    private static readonly Style Data = new(Color.Aqua);
    private static readonly Style Seat = new(Color.Fuchsia);
    private static readonly Style Warn = new(Color.Yellow);
    private static readonly Style Good = new(Color.Green);
    private static readonly Style Bad = new(Color.Red, decoration: Decoration.Bold);
    private static readonly Style Muted = new(Color.Grey);
    private static readonly Style Plain = Style.Plain;

    private readonly Lock _gate = new();
    private readonly IAnsiConsole _console;
    private readonly ConsoleGlyphs _glyphs;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ModelCallKey, ActiveModelCall> _activeCalls = [];
    private readonly Queue<IRenderable> _buffered = new();
    private PendingWait? _pendingWait;
    private int _spinnerFrame;
    private bool _liveDisplay;

    public SpectreLogRenderer(
        IAnsiConsole console,
        bool liveDisplay,
        ConsoleGlyphs? glyphs = null,
        TimeProvider? timeProvider = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _liveDisplay = liveDisplay;
        _glyphs = glyphs ?? ConsoleGlyphs.For(console);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal int ActiveCallCount
    {
        get
        {
            lock (_gate)
            {
                return _activeCalls.Count;
            }
        }
    }

    /// <summary>Whether anything on screen changes with time and not with the next log event.</summary>
    /// <remarks>
    /// This is what keeps an idle run from repainting four times a second. The live loop still
    /// wakes on its timer, but it draws nothing while this is false.
    /// </remarks>
    internal bool HasLiveWork
    {
        get
        {
            lock (_gate)
            {
                return _activeCalls.Count > 0 || _pendingWait is not null;
            }
        }
    }

    /// <summary>Moves the spinner on by one frame.</summary>
    /// <remarks>
    /// The live loop calls this, not <see cref="BuildLiveDisplay"/>. Building the picture has no
    /// side effect, so a test can build it as often as it likes and always get the same frame.
    /// </remarks>
    internal void AdvanceSpinner()
    {
        lock (_gate)
        {
            _spinnerFrame++;
        }
    }

    public void Process(SpectreLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            IRenderable? renderable;

            try
            {
                renderable = Render(entry);
            }
            catch
            {
                // A view that cannot draw an event must still report that the event happened.
                Emit(entry, Fallback(entry));
                return;
            }

            if (renderable is not null)
            {
                Emit(entry, renderable);
            }
        }
    }

    /// <summary>The whole live picture: the log tail, then the seats that are waiting.</summary>
    public IRenderable BuildLiveDisplay()
    {
        lock (_gate)
        {
            if (!_liveDisplay)
            {
                return new Rows();
            }

            // The refresh is now periodic, so only render the tail the window can show. Spectre
            // crops as well, but it crops after it has drawn all of it.
            var footer = (_activeCalls.Count > 0 ? _activeCalls.Count + 4 : 0)
                         + (_pendingWait is null ? 0 : 3);
            var visible = Math.Max(1, _console.Profile.Height - footer - 1);
            var rows = new List<IRenderable>(_buffered.Skip(Math.Max(0, _buffered.Count - visible)));

            if (_activeCalls.Count > 0)
            {
                rows.Add(Text.Empty);
                rows.Add(BuildActiveCallTable());
            }

            if (_pendingWait is { } wait)
            {
                rows.Add(Text.Empty);
                rows.Add(BuildWaitPanel(wait));
            }

            return rows.Count == 0 ? new Rows() : new Rows(rows);
        }
    }

    /// <summary>Gives the console back and writes everything the display still held.</summary>
    public void DisableLiveDisplay(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock (_gate)
        {
            _liveDisplay = false;
            _activeCalls.Clear();
            _pendingWait = null;

            while (_buffered.Count > 0)
            {
                WriteDirect(_buffered.Dequeue());
            }

            WriteDirect(Gutter(
                _timeProvider.GetUtcNow(),
                _glyphs.Fail,
                Bad,
                "console",
                Bad,
                Safe($"Live view disabled: {error.Message}", MessageLimit, Bad)));
        }
    }

    private void Emit(SpectreLogEntry entry, IRenderable renderable)
    {
        if (_liveDisplay)
        {
            _buffered.Enqueue(renderable);

            while (_buffered.Count > MaxBufferedLines)
            {
                _buffered.Dequeue();
            }

            return;
        }

        try
        {
            // Every renderable this class produces closes its own line. A second newline here
            // would put a blank line between every two events.
            _console.Write(renderable);
        }
        catch
        {
            WriteFallback(entry);
        }
    }

    private void WriteDirect(IRenderable renderable)
    {
        try
        {
            _console.Write(renderable);
        }
        catch
        {
            // There is no safe presentation fallback left.
        }
    }

    private IRenderable? Render(SpectreLogEntry entry)
    {
        if (IsChatEvent(entry.EventId))
        {
            return RenderChat(entry);
        }

        return entry.EventId.Id switch
        {
            1001 => RenderRunStarted(entry),
            1002 => RenderRunStopped(entry),
            1003 => RenderRoomSpend(entry),
            1004 => RenderCycleStarted(entry),
            1005 => RenderCycleFinished(entry),
            1006 => RenderCycleWaiting(entry),
            2001 => RenderAccount(entry),
            2002 => RenderCandidates(entry),
            2003 => Marked(entry, "hold", Warn),
            2004 => Event(entry, _glyphs.Up, Good, "order", Good, Safe(entry.Message, MessageLimit)),
            2005 => Event(entry, _glyphs.Down, Good, "close", Good, Safe(entry.Message, MessageLimit)),
            2006 => Event(entry, _glyphs.Warn, Warn, "risk", Warn, Safe(entry.Message, MessageLimit)),
            2007 => RenderCatalogFilter(entry),
            3001 => RenderProposal(entry),
            3002 => Marked(entry, "precheck", Warn),
            3003 => RenderAnalysis(entry),
            3004 => RenderDiscussion(entry),
            3005 => Marked(entry, "rebuttal", Seat),
            3006 => RenderVote(entry),
            3007 => RenderVerdict(entry),
            _ => RenderGeneric(entry),
        };
    }

    private IRenderable? RenderChat(SpectreLogEntry entry)
    {
        var kind = entry.EventId.Id % 10;

        if (kind == (int)ChatEvent.Sending)
        {
            // Made safe once, here. This record is read again on every live refresh, and the
            // seat, the phase, and the model name all arrive from configuration or a provider.
            var call = new ActiveModelCall(
                Clip(entry.Text("Persona", "unknown"), 24),
                Clip(entry.Text("Phase", "work"), 24),
                Clip(entry.Text("Model", "unknown model"), 40),
                entry.Timestamp("SentUtc"),
                Join(
                    $"{Number(entry.Integer("Messages"), "N0")} messages",
                    $"{Number(entry.Integer("Characters"), "N0")} chars",
                    $"{Number(entry.Integer("Tools"), "N0")} tools"));

            _activeCalls[new ModelCallKey(call.Persona, call.Phase)] = call;

            // The live table already shows this call, and shows it for as long as it runs.
            return _liveDisplay
                ? null
                : Event(
                    entry,
                    _glyphs.Active,
                    Seat,
                    call.Persona,
                    Seat,
                    Line(
                        ($"{call.Phase} ", Plain),
                        ($"{_glyphs.Arrow} {call.Model}", Muted),
                        (_glyphs.Separator, Muted),
                        (call.Summary, Muted)));
        }

        if (kind == (int)ChatEvent.Finished)
        {
            var persona = Clip(entry.Text("Persona", "unknown"), 24);
            var phase = Clip(entry.Text("Phase", "work"), 24);
            _activeCalls.Remove(new ModelCallKey(persona, phase));

            if (entry.Level >= LogLevel.Error)
            {
                return Event(
                    entry,
                    _glyphs.Fail,
                    Bad,
                    persona,
                    Bad,
                    Line(($"{phase} ", Plain), (Clip(entry.Message, MessageLimit), Bad)));
            }

            var tokens = entry.Long("Input") + entry.Long("Output");

            return Event(
                entry,
                _glyphs.Ok,
                Good,
                persona,
                Seat,
                Line(
                    ($"{phase} ", Plain),
                    (Join(
                        Duration(entry.Double("Seconds")),
                        $"{Number(tokens, "N0")} tokens",
                        $"{Number(entry.Integer("ToolCalls"), "N0")} tool call(s) "
                        + $"[{Clip(entry.Text("Tools", "none"), 60)}]"), Muted)));
        }

        // The plain file owns the complete transcript. Important transcript faults still show.
        return entry.Level >= LogLevel.Warning ? RenderGeneric(entry) : null;
    }

    private IRenderable RenderRunStarted(SpectreLogEntry entry) =>
        Panel(
            "AUTONOMOUS OPTIONS RUN",
            KeyValueGrid(
                ("Mode", entry.Text("Mode")),
                ("Safety", entry.Boolean("DryRun")
                    ? "DRY RUN - no broker orders"
                    : "LIVE PAPER ORDERS"),
                ("Seats", entry.Text("Seats", "none"))),
            Accent);

    private IRenderable RenderRunStopped(SpectreLogEntry entry) =>
        Panel(
            "RUN COMPLETE",
            KeyValueGrid(
                ("Cycles", Number(entry.Integer("Cycles"), "N0")),
                ("Total cost", entry.Text("Cost", "none"))),
            Accent);

    private IRenderable RenderRoomSpend(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Info,
        Muted,
        "spend",
        Muted,
        Line(
            ($"{Clip(entry.Text("Persona"), 24)} ", Seat),
            (Clip(entry.Text("Model"), 40), Muted),
            (_glyphs.Separator, Muted),
            (Join(
                $"{Number(entry.Integer("Calls"), "N0")} calls",
                $"{Number(entry.Long("Tokens"), "N0")} tokens"), Plain),
            (_glyphs.Separator, Muted),
            (Clip(entry.Text("Usd", "unpriced"), 24), Warn)));

    /// <remarks>
    /// The live view owns this event as a countdown, so it draws no line of its own there. The
    /// static view has no countdown and states the wait once.
    /// </remarks>
    private IRenderable? RenderCycleWaiting(SpectreLogEntry entry)
    {
        var reason = Clip(entry.Text("Reason", "until the next cycle"), 48);
        var resumeAt = entry.Timestamp("ResumeUtc");
        _pendingWait = new PendingWait(reason, resumeAt);

        return _liveDisplay
            ? null
            : Event(
                entry,
                _glyphs.Info,
                Muted,
                "waiting",
                Muted,
                Line(
                    ($"{Number(entry.Double("Minutes"), "N0")} minute(s) {reason}", Plain),
                    (_glyphs.Separator, Muted),
                    ($"resumes {Time(resumeAt)} UTC", Muted)));
    }

    private IRenderable RenderCycleStarted(SpectreLogEntry entry)
    {
        // The wait is over the moment a cycle starts, whatever the countdown still said.
        _pendingWait = null;

        var open = entry.Boolean("Open");
        var title = Join(
            $"CYCLE {Number(entry.Integer("Number"), "N0")}",
            $"{Time(entry.Timestamp("At"))} UTC",
            open ? "MARKET OPEN" : "MARKET CLOSED");

        return new Rule(Markup.Escape($" {title} "))
        {
            Justification = Justify.Left,
            Style = open ? Accent : Warn,
            Border = _glyphs.Box,
        };
    }

    private IRenderable RenderCycleFinished(SpectreLogEntry entry)
    {
        // A seat that never reported a finish would otherwise hold a live row for the whole run.
        _activeCalls.Clear();

        var grid = new Grid();
        grid.AddColumn(new GridColumn { NoWrap = true, Padding = new Padding(0, 0, 2, 0) });
        grid.AddColumn(new GridColumn { Alignment = Justify.Right });

        void Row(string key, string value, Style style) =>
            grid.AddRow(new Text(key, Muted), new Text(value, style));

        Row("Cycle", Number(entry.Integer("Number"), "N0"), Accent);
        Row("Candidates", Number(entry.Integer("Offered"), "N0"), Data);
        Row("Open orders", Number(entry.Integer("Opened"), "N0"), Good);
        Row("Close orders", Number(entry.Integer("CloseSubmitted"), "N0"), Good);
        Row("Confirmed closed", Number(entry.Integer("Closed"), "N0"), Good);
        Row("Rejected", Number(entry.Integer("Rejected"), "N0"), Warn);
        Row("Equity", $"{Number(entry.Decimal("Equity"), "N2")} USD", Data);
        Row("Run cost", ConsoleText.Safe(entry.Text("Cost", "none"), 60), Seat);

        return Panel("CYCLE SUMMARY", grid, Accent);
    }

    private IRenderable RenderAccount(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Info,
        Data,
        "account",
        Data,
        Line((Join(
            $"equity {Number(entry.Decimal("Equity"), "N2")} USD",
            $"cash {Number(entry.Decimal("Cash"), "N2")} USD",
            $"{Number(entry.Integer("Positions"), "N0")} position(s)"), Plain)));

    private IRenderable RenderCandidates(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Info,
        Data,
        "catalog",
        Data,
        Line(
            ($"{Number(entry.Integer("Offered"), "N0")} tradeable candidate(s)", Plain),
            ($" from {Number(entry.Integer("Scanned"), "N0")} tracked symbol(s)", Muted)));

    private IRenderable RenderCatalogFilter(SpectreLogEntry entry)
    {
        if (entry.Value("Reasons") is not EventCountBreakdown breakdown)
        {
            return Event(
                entry, _glyphs.Note, Data, "filter", Data, Safe(entry.Message, MessageLimit));
        }

        var dropped = entry.Properties.ContainsKey("Dropped");
        var noun = dropped ? "contract(s)" : "symbol(s)";
        var total = dropped ? entry.Integer("Dropped") : entry.Integer("Skipped");

        var grid = NewGutterGrid();
        AddGutterRow(
            grid,
            entry.At,
            _glyphs.Note,
            Data,
            "filter",
            Data,
            Line(($"removed {Number(total, "N0")} {noun}", Plain)));

        // An empty catalog stops the cycle, so the gate that refused each contract is the whole
        // story. One aligned row per gate reads faster than a bordered table.
        foreach (var pair in breakdown.Counts)
        {
            AddContinuationRow(grid, Line(
                (Humanize(pair.Key).PadRight(ReasonWidth), Muted),
                (Number(pair.Value, "N0").PadLeft(CountWidth), Plain)));
        }

        return grid;
    }

    private IRenderable RenderProposal(SpectreLogEntry entry)
    {
        var grid = NewGutterGrid();
        AddGutterRow(
            grid,
            entry.At,
            _glyphs.Note,
            Seat,
            "proposal",
            Seat,
            Line(
                ($"{Clip(entry.Text("Id"), 40)}", Seat),
                (_glyphs.Separator, Muted),
                ($"{Number(entry.Integer("Count"), "N0")} action(s)", Plain)));
        AddContinuationRow(grid, Safe(entry.Text("Thesis", "No thesis."), ThesisLimit));

        return grid;
    }

    private IRenderable RenderAnalysis(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Note,
        Seat,
        entry.Text("Persona"),
        Seat,
        Line(
            ("analysis ", Muted),
            ($"{Clip(entry.Text("Vote"), 16)} ", VoteStyle(entry.Text("Vote"))),
            (Confidence(entry.Value("Confidence")), Muted),
            (_glyphs.Separator, Muted),
            (Clip(entry.Text("Analysis"), AnalysisLimit), Plain)));

    private IRenderable RenderDiscussion(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Info,
        Seat,
        entry.Text("Speaker"),
        Seat,
        Line(
            ($"round {Number(entry.Integer("Round"), "N0")}", Muted),
            (_glyphs.Separator, Muted),
            (Clip(entry.Text("Summary"), DiscussionLimit), Plain)));

    private IRenderable RenderVote(SpectreLogEntry entry) => Event(
        entry,
        _glyphs.Note,
        Seat,
        entry.Text("Persona"),
        Seat,
        Line(
            ("vote ", Muted),
            ($"{Clip(entry.Text("Vote"), 16)} ", VoteStyle(entry.Text("Vote"))),
            (Confidence(entry.Value("Confidence")), Muted),
            (_glyphs.Separator, Muted),
            (Clip(entry.Text("Rationale"), RationaleLimit), Plain)));

    private IRenderable RenderVerdict(SpectreLogEntry entry)
    {
        var approved = string.Equals(
            entry.Text("Verdict"), "APPROVED", StringComparison.OrdinalIgnoreCase);

        return Panel(
            $"{(approved ? "APPROVED" : "REJECTED")} {Clip(entry.Text("Id"), 40)}",
            KeyValueGrid(
                ("Vote", entry.Text("Tally")),
                ("Cost", entry.Text("Cost"))),
            approved ? Good : Warn);
    }

    private IRenderable RenderGeneric(SpectreLogEntry entry)
    {
        var (symbol, style) = entry.Level switch
        {
            LogLevel.Critical => (_glyphs.Fail, Bad),
            LogLevel.Error => (_glyphs.Fail, Bad),
            LogLevel.Warning => (_glyphs.Warn, Warn),
            LogLevel.Information => (_glyphs.Info, Plain),
            _ => (_glyphs.Info, Muted),
        };

        var label = entry.Category == "Trader" ? string.Empty : ShortCategory(entry.Category);

        var grid = NewGutterGrid();
        AddGutterRow(
            grid, entry.At, symbol, style, label, Muted, Safe(entry.Message, MessageLimit));

        if (entry.Exception is not null)
        {
            AddContinuationRow(grid, Safe(
                $"{entry.Exception.GetType().Name}: {entry.Exception.Message}",
                MessageLimit,
                Bad));
        }

        return grid;
    }

    private Table BuildActiveCallTable()
    {
        var table = new Table
        {
            Border = _glyphs.Table,
            BorderStyle = Muted,
            Expand = true,
            ShowHeaders = true,
        };

        table.AddColumn(new TableColumn(new Text(" ", Muted)).NoWrap());
        table.AddColumn(new TableColumn(new Text("seat", Muted)).NoWrap());
        table.AddColumn(new TableColumn(new Text("phase", Muted)).NoWrap());
        table.AddColumn(new TableColumn(new Text("model", Muted)).NoWrap());
        table.AddColumn(new TableColumn(new Text("for", Muted)).RightAligned().NoWrap());
        table.AddColumn(new TableColumn(new Text("request", Muted)));

        var now = _timeProvider.GetUtcNow();
        var frame = _glyphs.Frame(_spinnerFrame);

        foreach (var call in _activeCalls.Values
                     .OrderBy(call => call.Persona, StringComparer.Ordinal)
                     .ThenBy(call => call.Phase, StringComparer.Ordinal))
        {
            table.AddRow(
                new Text(frame, Seat),
                new Text(call.Persona, Seat),
                new Text(call.Phase, Plain),
                new Text(call.Model, Muted),
                new Text(Elapsed(now - call.StartedAt), Warn),
                new Text(call.Summary, Muted));
        }

        return table;
    }

    /// <summary>The countdown to the next cycle, drawn while the session is idle.</summary>
    private Panel BuildWaitPanel(PendingWait wait)
    {
        var remaining = wait.ResumeAt - _timeProvider.GetUtcNow();

        return new Panel(Line(
            ($"{_glyphs.Frame(_spinnerFrame)}  ", Accent),
            (wait.Reason, Plain),
            (_glyphs.Separator, Muted),
            ($"{Elapsed(remaining)} left", Warn),
            (_glyphs.Separator, Muted),
            ($"resumes {Time(wait.ResumeAt)} UTC", Muted)))
        {
            Border = _glyphs.Box,
            BorderStyle = Muted,
            Expand = true,
            Padding = new Padding(1, 0),
        };
    }

    private IRenderable Marked(SpectreLogEntry entry, string label, Style style) =>
        Event(entry, _glyphs.Note, style, label, style, Safe(entry.Message, MessageLimit));

    /// <summary>One gutter line: time, symbol, label, message.</summary>
    private static IRenderable Event(
        SpectreLogEntry entry,
        string symbol,
        Style symbolStyle,
        string label,
        Style labelStyle,
        IRenderable message) =>
        Gutter(entry.At, symbol, symbolStyle, label, labelStyle, message);

    private static IRenderable Gutter(
        DateTimeOffset at,
        string symbol,
        Style symbolStyle,
        string label,
        Style labelStyle,
        IRenderable message)
    {
        var grid = NewGutterGrid();
        AddGutterRow(grid, at, symbol, symbolStyle, label, labelStyle, message);
        return grid;
    }

    /// <remarks>
    /// The message column takes the width that is left, so Spectre wraps long prose inside it.
    /// That is what gives a wrapped line its hanging indent, with no hard-coded spaces.
    /// </remarks>
    private static Grid NewGutterGrid()
    {
        var grid = new Grid { Expand = true };
        grid.AddColumn(new GridColumn
        {
            Width = TimeWidth,
            NoWrap = true,
            Padding = new Padding(0, 0, 2, 0),
        });
        grid.AddColumn(new GridColumn
        {
            Width = SymbolWidth,
            NoWrap = true,
            Padding = new Padding(0, 0, 2, 0),
        });
        grid.AddColumn(new GridColumn
        {
            Width = LabelWidth,
            NoWrap = true,
            Padding = new Padding(0, 0, 2, 0),
        });
        grid.AddColumn(new GridColumn { Padding = new Padding(0, 0, 0, 0) });

        return grid;
    }

    private static void AddGutterRow(
        Grid grid,
        DateTimeOffset at,
        string symbol,
        Style symbolStyle,
        string label,
        Style labelStyle,
        IRenderable message) =>
        grid.AddRow(
            new Text(Time(at), Muted),
            new Text(symbol, symbolStyle),
            new Text(ConsoleText.Safe(label, LabelWidth, string.Empty), labelStyle),
            message);

    private static void AddContinuationRow(Grid grid, IRenderable message) =>
        grid.AddRow(Text.Empty, Text.Empty, Text.Empty, message);

    /// <summary>The last resort, for an event whose own renderer threw. It closes its own line.</summary>
    private static IRenderable Fallback(SpectreLogEntry entry)
    {
        var grid = new Grid { Expand = true };
        grid.AddColumn(new GridColumn());
        grid.AddRow(new Text(
            $"{Time(entry.At)} {entry.Level.ToString().ToUpperInvariant()} "
            + $"[{entry.EventId.Id}] {ConsoleText.Safe(entry.Message, MessageLimit)}",
            Muted));

        return grid;
    }

    private Panel Panel(string header, IRenderable content, Style style) => new(content)
    {
        Header = new PanelHeader(Markup.Escape($" {ConsoleText.Safe(header, 80)} ")),
        Border = _glyphs.Box,
        BorderStyle = style,
        Expand = true,
        Padding = new Padding(1, 0),
    };

    private static Grid KeyValueGrid(params (string Key, string Value)[] values)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn { NoWrap = true, Padding = new Padding(0, 0, 2, 0) });
        grid.AddColumn(new GridColumn());

        foreach (var (key, value) in values)
        {
            grid.AddRow(
                new Text(key, Muted),
                new Text(ConsoleText.Safe(value, MessageLimit), Plain));
        }

        return grid;
    }

    private static Paragraph Line(params (string Value, Style Style)[] segments)
    {
        var line = new Paragraph();
        foreach (var (value, style) in segments)
        {
            line.Append(value, style);
        }

        return line;
    }

    /// <summary>Untrusted text, made safe and given a style.</summary>
    private static IRenderable Safe(string? value, int limit) => Safe(value, limit, Plain);

    private static IRenderable Safe(string? value, int limit, Style style) =>
        new Text(ConsoleText.Safe(value, limit), style);

    private string Clip(string? value, int limit) =>
        ConsoleText.Safe(value, limit, _glyphs.Ellipsis);

    private string Join(params string[] parts) => string.Join(_glyphs.Separator, parts);

    private static string Time(DateTimeOffset at) =>
        at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>How long a finished call took.</summary>
    /// <remarks>
    /// A short call is easier to compare in seconds and a long one is not: <c>444.0s</c> makes
    /// the reader do the division that <c>7:24</c> has already done. The seat table counts the
    /// same call up in <c>m:ss</c>, so a long call ends in the units it was measured in.
    /// </remarks>
    private static string Duration(double seconds) => seconds < 60
        ? $"{Number(seconds, "F1")}s"
        : Elapsed(TimeSpan.FromSeconds(seconds));

    /// <summary>A duration an operator reads at a glance. Never negative.</summary>
    /// <remarks>
    /// A countdown reaches zero before the session wakes, and a clock that moves back would
    /// otherwise print a negative time. Zero is the honest answer for both.
    /// </remarks>
    private static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static bool IsChatEvent(EventId id) => id.Id is >= 4101 and <= 4999;

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    private static string Confidence(object? value)
    {
        var confidence = Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture);
        return confidence.ToString("P0", Percent);
    }

    private static string Number(IFormattable value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static NumberFormatInfo CreatePercentFormat()
    {
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.PercentPositivePattern = 1;
        format.PercentNegativePattern = 1;

        return NumberFormatInfo.ReadOnly(format);
    }

    private static Style VoteStyle(string vote) =>
        vote.Contains("APPROVE", StringComparison.OrdinalIgnoreCase)
            ? Good
            : vote.Contains("REJECT", StringComparison.OrdinalIgnoreCase)
                ? Warn
                : Muted;

    private static string Humanize(string value) => string.Join(
        ' ',
        value.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select((word, index) => index == 0 && word.Length > 0
                ? char.ToUpperInvariant(word[0]) + word[1..]
                : word));

    private void WriteFallback(SpectreLogEntry entry)
    {
        try
        {
            _console.Profile.Out.Writer.WriteLine(
                $"{entry.At:O} {entry.Level.ToString().ToUpperInvariant()} "
                + $"{entry.Category} [{entry.EventId.Id}] - "
                + ConsoleText.Safe(entry.Message, MessageLimit));
        }
        catch
        {
            // Logging must not change a trading decision or hide the original exception.
        }
    }

    private readonly record struct ModelCallKey(string Persona, string Phase);

    /// <summary>An idle session, and the instant it wakes.</summary>
    private readonly record struct PendingWait(string Reason, DateTimeOffset ResumeAt);

    private sealed record ActiveModelCall(
        string Persona,
        string Phase,
        string Model,
        DateTimeOffset StartedAt,
        string Summary);
}

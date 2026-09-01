using Spectre.Console;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>The symbols the operator view draws, and the ASCII stand-ins for them.</summary>
/// <remarks>
/// <para>
/// <b>A symbol the console encoding does not hold is not dropped. It is replaced.</b> A Windows
/// console on an OEM code page uses the best-fit table, which maps <c>•</c> to <c>0x07</c>, the
/// terminal bell, and <c>▲ ▼ →</c> to other C0 control bytes. The operator then hears a beep on
/// almost every line and reads a damaged layout.
/// </para>
/// <para>
/// <see cref="For"/> reads <see cref="IReadOnlyCapabilities.Unicode"/>, which Spectre computes
/// from the console encoding. Spectre already keeps its own borders safe this way. Only the
/// symbols this application writes need this table.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// The borders belong here too. Spectre substitutes a <i>safe</i> border for a console that has
/// no rounded corners, but a safe border is still box drawing: <c>TableBorder.Rounded</c> falls
/// back to <c>┌─┐</c>, not to <c>+-+</c>. A console that cannot hold <c>●</c> usually cannot hold
/// <c>┌</c> either, so the whole view moves to ASCII together or none of it does.
/// </para>
/// <para>
/// The spinner is a Spectre <see cref="Spectre.Console.Spinner"/> because its frames are already
/// written and already divided into a Unicode set and an ASCII set. Only the frames are used.
/// The animation itself is driven by the live refresh, not by the spinner interval.
/// </para>
/// </remarks>
public sealed record ConsoleGlyphs(
    string Info,
    string Active,
    string Ok,
    string Fail,
    string Warn,
    string Note,
    string Up,
    string Down,
    string Arrow,
    string Ellipsis,
    string Separator,
    BoxBorder Box,
    TableBorder Table,
    Spinner Spinner)
{
    /// <summary>The set for a console that encodes UTF-8 or UTF-16.</summary>
    public static readonly ConsoleGlyphs Unicode = new(
        Info: "·",
        Active: "●",
        Ok: "✓",
        Fail: "✗",
        Warn: "!",
        Note: "◆",
        Up: "▲",
        Down: "▼",
        Arrow: "→",
        Ellipsis: "…",
        Separator: " · ",
        Box: BoxBorder.Rounded,
        Table: TableBorder.Rounded,
        Spinner: Spinner.Known.Dots);

    /// <summary>The set for every other console. Each symbol is one ASCII character.</summary>
    public static readonly ConsoleGlyphs Ascii = new(
        Info: ".",
        Active: "*",
        Ok: "+",
        Fail: "x",
        Warn: "!",
        Note: "~",
        Up: "^",
        Down: "v",
        Arrow: "->",
        Ellipsis: "...",
        Separator: " - ",
        Box: BoxBorder.Ascii,
        Table: TableBorder.Ascii,
        Spinner: Spinner.Known.Line);

    /// <summary>The spinner frame for a refresh count. The renderer owns the count.</summary>
    public string Frame(int tick)
    {
        var frames = Spinner.Frames;

        return frames.Count == 0 ? Active : frames[(int)((uint)tick % frames.Count)];
    }

    /// <summary>The set this console can encode.</summary>
    public static ConsoleGlyphs For(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);

        return console.Profile.Capabilities.Unicode ? Unicode : Ascii;
    }
}

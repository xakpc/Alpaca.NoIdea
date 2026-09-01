using System.Globalization;
using System.Text;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>Makes model, tool, and exception text safe to draw on a terminal.</summary>
/// <remarks>
/// <para>
/// A model writes prose, a tool answers with whatever a web page held, and an exception carries
/// whatever a library put in it. None of that is terminal input. A control character in that text
/// rings the bell, moves the cursor, or starts an escape sequence, and a newline inside a one-line
/// event breaks the column layout.
/// </para>
/// <para>
/// <b>This is the only gate between untrusted text and the terminal.</b> Spectre neutralizes its
/// own markup with <see cref="Spectre.Console.Text"/>. It does not remove control characters.
/// </para>
/// </remarks>
public static class ConsoleText
{
    /// <summary>Pass this as the limit to keep the whole string.</summary>
    public const int NoLimit = int.MaxValue;

    /// <summary>
    /// Removes every control character, folds each run of whitespace into one space, and clips to
    /// <paramref name="limit"/> characters on a boundary that keeps the text well formed.
    /// </summary>
    /// <remarks>
    /// The clip counts Unicode scalar values, so it never cuts a surrogate pair in half. A cut can
    /// still separate a combining mark from its base letter. That is a strange-looking letter, not
    /// a broken string, and it costs far less than grapheme segmentation on every log line.
    /// </remarks>
    public static string Safe(string? value, int limit = NoLimit, string ellipsis = "...")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        Span<char> buffer = stackalloc char[2];
        var pendingSpace = false;
        var kept = 0;
        var clipped = false;

        // EnumerateRunes replaces an unpaired surrogate with U+FFFD, so damaged input cannot
        // reach the terminal as an invalid sequence either.
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            // A format character is invisible but not harmless: a bidirectional override reorders
            // the rest of the line, so the operator reads text the log does not hold.
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                continue;
            }

            if (kept >= limit)
            {
                clipped = true;
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(buffer[..rune.EncodeToUtf16(buffer)]);
            kept++;
        }

        return clipped ? builder.Append(ellipsis).ToString() : builder.ToString();
    }
}

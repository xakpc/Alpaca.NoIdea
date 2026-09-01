using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>
/// The events a person watches a run by.
/// </summary>
/// <remarks>
/// <para>
/// An id is what a filter selects on, so a view can show a few of these without the trading
/// code knowing that a view exists. An earlier design put a second reporting interface next
/// to the log to get the same result. <see cref="ILogger"/> already has the mechanism.
/// </para>
/// <para>
/// <b>A numbered line is run narrative. An unnumbered line is diagnostic.</b> This split is
/// what makes "show only these ids" a correct filter and not a guess.
/// </para>
/// <para>
/// <b>An id is permanent.</b> If you give an event a new number, every filter that selects
/// the old number stops showing it, and nothing reports the change.
/// </para>
/// </remarks>
public static class RunEvents
{
    // 1000 - the host and the session
    public static readonly EventId RunStarted = new(1001, nameof(RunStarted));
    public static readonly EventId RunStopped = new(1002, nameof(RunStopped));
    public static readonly EventId RoomSpend = new(1003, nameof(RoomSpend));
    public static readonly EventId CycleStarted = new(1004, nameof(CycleStarted));
    public static readonly EventId CycleFinished = new(1005, nameof(CycleFinished));

    /// <summary>The session is idle until the next cycle. Says when it resumes and why it waits.</summary>
    /// <remarks>
    /// The wait is half an hour by default. Without this event the console shows nothing at all
    /// for that long, and a live run cannot be told apart from a hung one.
    /// </remarks>
    public static readonly EventId CycleWaiting = new(1006, nameof(CycleWaiting));

    // 2000 - the trading loop
    public static readonly EventId AccountRead = new(2001, nameof(AccountRead));
    public static readonly EventId CandidatesBuilt = new(2002, nameof(CandidatesBuilt));
    public static readonly EventId Hold = new(2003, nameof(Hold));
    public static readonly EventId OrderDecided = new(2004, nameof(OrderDecided));
    public static readonly EventId PositionClosed = new(2005, nameof(PositionClosed));
    public static readonly EventId RiskRejected = new(2006, nameof(RiskRejected));
    public static readonly EventId CatalogFiltered = new(2007, nameof(CatalogFiltered));

    // 3000 - the war room
    public static readonly EventId ProposalMade = new(3001, nameof(ProposalMade));
    public static readonly EventId ProposalRejectedEarly = new(3002, nameof(ProposalRejectedEarly));
    public static readonly EventId AnalysisReceived = new(3003, nameof(AnalysisReceived));
    public static readonly EventId DiscussionHeard = new(3004, nameof(DiscussionHeard));
    public static readonly EventId RebuttalMade = new(3005, nameof(RebuttalMade));
    public static readonly EventId VoteCast = new(3006, nameof(VoteCast));
    public static readonly EventId VerdictReached = new(3007, nameof(VerdictReached));

    // 4000 - the conversation with a model. Each seat owns a block of one hundred, and the
    // last digit is always the same kind of line. So `41xx` is everything the proposer said
    // and heard, and an id that ends in 3 is a tool call from any seat.
    //
    // A block is permanent, exactly as a single id is: a seat that moves to a new number
    // makes every filter that selects the old one go quiet, and nothing reports it.
    private static readonly Dictionary<string, int> PersonaBlocks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["proposer"] = 1,
            ["quant"] = 2,
            ["skeptic"] = 3,
            ["market"] = 4,
            ["exposure"] = 5,
        };

    /// <summary>The block for a seat this table does not know. Shared, on purpose.</summary>
    private const int UnknownPersonaBlock = 9;

    private static readonly ConcurrentDictionary<(string Persona, ChatEvent Kind), EventId> ChatIds =
        new();

    /// <summary>
    /// The id of one line of one seat's conversation with its model.
    /// </summary>
    /// <remarks>
    /// The name reads <c>proposer.ToolCall</c>, so a console line says which seat and which
    /// kind of line it is without the reader knowing the numbering.
    /// </remarks>
    public static EventId Chat(string persona, ChatEvent kind)
    {
        var name = string.IsNullOrWhiteSpace(persona) ? "unknown" : persona;

        return ChatIds.GetOrAdd(
            (name, kind),
            key =>
            {
                var block = PersonaBlocks.TryGetValue(key.Persona, out var known)
                    ? known
                    : UnknownPersonaBlock;

                return new EventId(
                    4000 + (block * 100) + (int)key.Kind,
                    $"{key.Persona}.{key.Kind}");
            });
    }
}

/// <summary>One kind of line in the conversation between a seat and its model.</summary>
/// <remarks>
/// The value is the last digit of the event id, so it is as permanent as the id. See
/// <see cref="RunEvents.Chat"/>.
/// </remarks>
public enum ChatEvent
{
    /// <summary>What the host sent: the system prompt, the payload, and the toolbox.</summary>
    Request = 1,

    /// <summary>What the model wrote in prose, including a reasoning summary.</summary>
    Said = 2,

    /// <summary>A tool the model called, with the arguments it passed.</summary>
    ToolCall = 3,

    /// <summary>What that tool answered.</summary>
    ToolResult = 4,

    /// <summary>The tally: finish reason, turns, tools called, tokens, and time.</summary>
    Finished = 5,

    /// <summary>
    /// One line, written immediately before the call goes out: which seat and phase, which model gets it, and how the call is configured.
    /// </summary>
    Sending = 6,
}

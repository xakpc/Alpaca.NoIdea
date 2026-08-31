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

    // 2000 - the trading loop
    public static readonly EventId AccountRead = new(2001, nameof(AccountRead));
    public static readonly EventId CandidatesBuilt = new(2002, nameof(CandidatesBuilt));
    public static readonly EventId Hold = new(2003, nameof(Hold));
    public static readonly EventId OrderDecided = new(2004, nameof(OrderDecided));
    public static readonly EventId PositionClosed = new(2005, nameof(PositionClosed));
    public static readonly EventId RiskRejected = new(2006, nameof(RiskRejected));

    // 3000 - the war room
    public static readonly EventId ProposalMade = new(3001, nameof(ProposalMade));
    public static readonly EventId ProposalRejectedEarly = new(3002, nameof(ProposalRejectedEarly));
    public static readonly EventId AnalysisReceived = new(3003, nameof(AnalysisReceived));
    public static readonly EventId DiscussionHeard = new(3004, nameof(DiscussionHeard));
    public static readonly EventId RebuttalMade = new(3005, nameof(RebuttalMade));
    public static readonly EventId VoteCast = new(3006, nameof(VoteCast));
    public static readonly EventId VerdictReached = new(3007, nameof(VerdictReached));
}

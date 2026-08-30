using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>Which model service a persona speaks through.</summary>
/// <remarks>
/// Grok is served by the OpenAI adapter pointed at x.ai, because the xAI API is
/// OpenAI-compatible. It is a distinct value rather than a base-URL setting so a persona names
/// its service plainly and picks up the right key.
/// </remarks>
public enum ModelProvider
{
    /// <summary>No model. A persona that computes its answer in plain C#.</summary>
    None = 0,

    Anthropic = 1,
    OpenAi = 2,
    Grok = 3,
}

/// <summary>Why the war room is sitting.</summary>
public enum WarRoomPurpose
{
    /// <summary>Find and judge a new trade.</summary>
    NewTrade = 0,

    /// <summary>Judge whether an open position's thesis still holds.</summary>
    PositionReview = 1,
}

/// <summary>How a persona lands on the operation under discussion.</summary>
public enum VoteKind
{
    /// <summary>No view. Counts toward neither side, and lowers nothing.</summary>
    Abstain = 0,

    /// <summary>Back the operation.</summary>
    Approve = 1,

    /// <summary>Oppose the operation.</summary>
    Reject = 2,
}

/// <summary>How strongly a speaker backs one contract while discussing.</summary>
public enum Stance
{
    Neutral = 0,
    Agree = 1,
    Doubt = 2,
    Oppose = 3,
}

/// <summary>One persona's view of one contract, said during discussion.</summary>
public sealed record ContractAssessment
{
    public required string ContractSymbol { get; init; }
    public required Stance Stance { get; init; }
    public decimal? Probability { get; init; }
    public required string Assessment { get; init; }
    public IReadOnlyList<string> Risks { get; init; } = [];
}

/// <summary>
/// One persona's first opinion, formed before it has seen anybody else's.
/// </summary>
/// <remarks>
/// The independent pass exists to stop the first speaker anchoring the room. It is the main
/// reason several agents beat one, so the initial vote is recorded <b>before</b> the analyses
/// are shared, and a later change of mind is therefore visible rather than invisible.
/// </remarks>
public sealed record PersonaAnalysis
{
    public required string Persona { get; init; }
    public required VoteKind InitialVote { get; init; }
    public decimal Confidence { get; init; } = 0.5m;
    public decimal? Probability { get; init; }
    public required string Analysis { get; init; }
    public IReadOnlyList<string> SupportingEvidence { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];

    /// <summary>Set when the persona failed. It is not counted as an approval.</summary>
    public string? Fault { get; init; }

    public bool Completed => Fault is null;

    public static PersonaAnalysis Failed(string persona, string fault) => new()
    {
        Persona = persona,
        InitialVote = VoteKind.Abstain,
        Confidence = 0m,
        Analysis = fault,
        Fault = fault,
    };
}

/// <summary>One thing a persona said in a discussion round.</summary>
public sealed record RoomContribution
{
    public required string Speaker { get; init; }
    public required int Round { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<ContractAssessment> Assessments { get; init; } = [];
    public string? Fault { get; init; }

    public bool Spoke => Fault is null;

    public static RoomContribution Failed(string speaker, int round, string fault) =>
        new() { Speaker = speaker, Round = round, Summary = "", Fault = fault };

    public static RoomContribution Silent(string speaker, int round) =>
        new() { Speaker = speaker, Round = round, Summary = "(nothing to add)" };
}

/// <summary>One persona's final position, cast privately after the debate.</summary>
/// <remarks>
/// <see cref="Confidence"/> weights the vote in the tally and therefore the position size.
/// <see cref="Probability"/> is what makes the persona accountable afterwards: a voice that
/// only narrates risk cannot be wrong, because risks always exist. A number can be scored
/// against the outcome.
/// </remarks>
public sealed record PersonaVote
{
    public required string Persona { get; init; }
    public required VoteKind Vote { get; init; }
    public decimal Confidence { get; init; } = 0.5m;
    public decimal? Probability { get; init; }
    public required string Rationale { get; init; }

    /// <summary>The largest risk this persona could not resolve. Spec §21.</summary>
    public string? UnresolvedRisk { get; init; }

    /// <summary>Set when the persona failed. A faulted vote is never an approval.</summary>
    public string? Fault { get; init; }

    public bool Cast => Fault is null;

    public static PersonaVote Abstained(string persona, string reason) => new()
    {
        Persona = persona,
        Vote = VoteKind.Abstain,
        Confidence = 0m,
        Rationale = reason,
        Fault = reason,
    };
}

/// <summary>The operation under judgement, before the room has weighed in.</summary>
/// <remarks>
/// Produced once by the proposer. The discussion does not edit it in place: the proposer may
/// replace it during rebuttal, and a replacement re-enters pre-validation (spec §20). The
/// votes change how large it is; <c>RiskGuard</c> then decides whether it may happen at all.
/// </remarks>
public sealed record ProposedOperation
{
    public IReadOnlyList<StrategyAction> Actions { get; init; } = [];

    /// <summary>Why, in the proposer's words. The room reads this.</summary>
    public required string Thesis { get; init; }

    /// <summary>What must stay true for the thesis to hold. Spec §10.15.</summary>
    public IReadOnlyList<string> ThesisConditions { get; init; } = [];

    public IReadOnlyList<string> MainRisks { get; init; } = [];

    /// <summary>A policy rewrite the proposer wants. Clamped before use.</summary>
    public Trading.StrategyPolicy? RevisedPolicy { get; init; }

    public bool TradesAnything =>
        Actions.Any(action => action.Kind != StrategyActionKind.Hold);

    /// <summary>A stable key over the economic substance, for detecting a real modification.</summary>
    /// <remarks>
    /// Spec §20: if the proposer changes symbol, strategy, expiration, strikes or legs, it is
    /// a new proposal and must be pre-validated again. Reasoning changes are not a
    /// modification.
    /// </remarks>
    public string SubstanceKey =>
        string.Join("|", Actions
            .Where(action => action.Kind != StrategyActionKind.Hold)
            .Select(action => $"{action.Kind}:{action.ContractSymbol}:{action.Contracts}")
            .OrderBy(key => key, StringComparer.Ordinal));

    public static ProposedOperation Nothing(string thesis) =>
        new() { Actions = [StrategyAction.Hold(thesis)], Thesis = thesis };
}

/// <summary>An open position the room is asked to judge. Spec §12.</summary>
public sealed record PositionUnderReview
{
    public required PositionState Position { get; init; }
    public required string TriggerReason { get; init; }

    /// <summary>The thesis the position was opened on, if it is known.</summary>
    public string? OriginalThesis { get; init; }

    public IReadOnlyList<string> OriginalThesisConditions { get; init; } = [];
    public decimal? UnrealizedPnlFraction { get; init; }
    public int? DaysToExpiration { get; init; }
}

/// <summary>
/// Everything a persona may see. Deliberately does not carry votes.
/// </summary>
/// <remarks>
/// <b>There is no votes field, and that is the point.</b> Spec §21 requires final votes to
/// stay hidden until every vote is in. Leaving the field out means privacy is a property of
/// the type rather than a rule a caller has to remember, so no future edit can leak one
/// vote into another persona's prompt.
/// </remarks>
public sealed record RoomContext
{
    public required string ProposalId { get; init; }
    public required WarRoomPurpose Purpose { get; init; }
    public required StrategyContext Market { get; init; }
    public required ProposedOperation Operation { get; init; }
    public required IReadOnlyList<StrategyActionKind> AllowedActions { get; init; }

    /// <summary>Set only for a position review.</summary>
    public PositionUnderReview? Position { get; init; }

    /// <summary>
    /// The independent analyses. <b>Empty during the independent pass</b>, so a persona
    /// forming its first opinion cannot see anybody else's.
    /// </summary>
    public IReadOnlyList<PersonaAnalysis> Analyses { get; init; } = [];

    /// <summary>Discussion so far, in order.</summary>
    public IReadOnlyList<RoomContribution> Said { get; init; } = [];

    public int Round { get; init; }
}

/// <summary>
/// One seat in the war room.
/// </summary>
/// <remarks>
/// <para>
/// A persona is a class, not a configuration row. It owns its model, provider and tools, and
/// two personas need not share any of them. A room of one model arguing with itself shares
/// that model's blind spots; independent errors are what make a second opinion worth its
/// tokens.
/// </para>
/// <para>
/// Nothing here mentions a model. A persona that computes concentration and time decay in
/// plain C# implements this interface exactly like one that calls an LLM, costs nothing, and
/// cannot hallucinate.
/// </para>
/// <para>
/// <b>A persona cannot trade.</b> It analyses, it discusses, and it votes. Only the proposer
/// produces an operation, and <c>RiskGuard</c> still decides whether that operation is
/// allowed.
/// </para>
/// </remarks>
public interface IPersona
{
    /// <summary>Short name. Appears in the transcript, the votes, and the audit trail.</summary>
    string Name { get; }

    /// <summary>Which service this persona costs money on. <c>None</c> for a C# persona.</summary>
    ModelProvider Provider { get; }

    /// <summary>Spec §18. A first opinion, formed without seeing anybody else's.</summary>
    Task<PersonaAnalysis> AnalyseAsync(RoomContext context, CancellationToken cancellationToken);

    /// <summary>Spec §19. Challenge the others, having read every analysis.</summary>
    Task<RoomContribution> ParticipateAsync(RoomContext context, CancellationToken cancellationToken);

    /// <summary>Spec §21. A final position, cast without seeing another vote.</summary>
    Task<PersonaVote> VoteAsync(RoomContext context, CancellationToken cancellationToken);
}

/// <summary>
/// The persona that opens the room: it searches, and it decides what to put forward.
/// </summary>
/// <remarks>
/// One seat proposes. It carries the full read-only Alpaca toolset because it is the seat
/// that has to look at the market rather than react to somebody else's reading of it.
/// </remarks>
public interface IProposingPersona : IPersona
{
    /// <summary>
    /// Spec §15. Returns an operation, or <see cref="ProposedOperation.Nothing"/> for
    /// <c>NO_TRADE</c>.
    /// </summary>
    Task<ProposedOperation> ProposeAsync(
        StrategyContext market,
        WarRoomPurpose purpose,
        PositionUnderReview? position,
        IReadOnlyList<StrategyActionKind> allowedActions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spec §20. Defend, modify, or withdraw after hearing the room. A modification
    /// re-enters pre-validation.
    /// </summary>
    Task<ProposedOperation> RebutAsync(RoomContext context, CancellationToken cancellationToken);
}

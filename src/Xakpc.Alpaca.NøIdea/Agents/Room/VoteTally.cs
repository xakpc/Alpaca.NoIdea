namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>What the room decided.</summary>
public enum WarRoomVerdict
{
    /// <summary>The proposer offered nothing. No reviewer was called.</summary>
    NoProposal = 0,

    /// <summary>C# rejected the proposal before the room spent tokens.</summary>
    PreValidationRejected = 1,

    /// <summary>The room did not back it.</summary>
    Rejected = 2,

    /// <summary>The room backed it. It still has to pass RiskGuard.</summary>
    Approved = 3,
}

/// <summary>
/// The arithmetic that turns votes into a verdict and a size.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic C#. No model touches this, because it decides how much money is at risk.
/// </para>
/// <para>
/// Confidence-weighted rather than a head count: a persona that is barely persuaded should
/// not cancel one that is certain. Abstentions lower the average without opposing, which is
/// the correct treatment of "I have no view" — it should dilute conviction, not defeat it.
/// </para>
/// <para>
/// Dilution alone is not consent. A room where every seat abstains has a net of exactly zero,
/// which clears any negative threshold, so silence used to buy. New money therefore needs one
/// seat that voted Approve as well. See <c>requireAnApproval</c> on <see cref="Count"/>.
/// </para>
/// </remarks>
public sealed record VoteTally
{
    public required int Approvals { get; init; }
    public required int Rejections { get; init; }
    public required int Abstentions { get; init; }

    /// <summary>Voters that failed. Spec §33.2: never counted as an approval.</summary>
    public required int Faults { get; init; }

    /// <summary>Weighted conviction, from -1 (all certain against) to +1 (all certain for).</summary>
    public required decimal Net { get; init; }

    /// <summary>How much of the proposed size to take, from 0 to 1.</summary>
    public required decimal SizeMultiplier { get; init; }

    public required bool QuorumMet { get; init; }

    /// <summary>Whether the room cleared the proposal.</summary>
    /// <remarks>
    /// The verdict, not the size, is the decision. A negative approve threshold deliberately
    /// clears a proposal whose net conviction is below zero, while <see cref="SizeMultiplier"/>
    /// floors at zero because nothing may size <em>up</em> on negative conviction. Reading the
    /// verdict off the multiplier therefore turned every such approval back into a rejection
    /// and made a negative threshold impossible to use.
    /// </remarks>
    public required bool Approved { get; init; }

    /// <summary>
    /// Applies the tally to a proposed contract count.
    /// </summary>
    /// <remarks>
    /// A cleared proposal always trades at least one contract: rounding a conviction down to
    /// zero would silently turn an approval into a rejection, which is a different decision
    /// wearing the same name. The floor keys off <see cref="Approved"/> and not the multiplier,
    /// because a proposal cleared on a negative threshold has a zero multiplier and must still
    /// trade the minimum.
    /// </remarks>
    public int ContractsFor(int proposed)
    {
        if (!Approved || proposed <= 0)
        {
            return 0;
        }

        var scaled = (int)Math.Round(proposed * SizeMultiplier, MidpointRounding.AwayFromZero);
        return Math.Clamp(scaled, 1, proposed);
    }

    /// <summary>
    /// Counts the votes.
    /// </summary>
    /// <param name="votes">One per required voter.</param>
    /// <param name="approveThreshold">
    /// The conviction a proposal must exceed. 0 means "more weighted conviction for than
    /// against". Raising it approaches the spec's 4-of-5 recommendation. This is the single
    /// number most likely to decide whether the system ever trades.
    /// </param>
    /// <param name="requireEveryVoter">
    /// Spec §33.2 and §22. When true, a missing or faulted vote rejects the proposal for this
    /// cycle rather than proceeding on a smaller quorum.
    /// </param>
    /// <param name="requireAnApproval">
    /// When true, a proposal also needs one seat that voted Approve. A room that only abstains
    /// has a net of exactly zero, which clears a negative threshold on silence alone. That is
    /// how one open was authorised while two seats stated a profit probability of 0.41 and no
    /// seat backed the trade. Use it for new money. A close must stay easy to authorise, so the
    /// position-review path leaves it false.
    /// </param>
    public static VoteTally Count(
        IReadOnlyList<PersonaVote> votes,
        decimal approveThreshold = 0m,
        bool requireEveryVoter = true,
        bool requireAnApproval = false)
    {
        ArgumentNullException.ThrowIfNull(votes);

        if (votes.Count == 0)
        {
            return new VoteTally
            {
                Approvals = 0,
                Rejections = 0,
                Abstentions = 0,
                Faults = 0,
                Net = 0m,
                SizeMultiplier = 0m,
                QuorumMet = false,
                Approved = false,
            };
        }

        var faults = votes.Count(vote => !vote.Cast);
        var approvals = votes.Count(vote => vote.Cast && vote.Vote == VoteKind.Approve);
        var rejections = votes.Count(vote => vote.Cast && vote.Vote == VoteKind.Reject);
        var abstentions = votes.Count(vote => vote.Cast && vote.Vote == VoteKind.Abstain);

        var weighted = votes
            .Where(vote => vote.Cast)
            .Sum(vote => vote.Vote switch
            {
                VoteKind.Approve => Math.Clamp(vote.Confidence, 0m, 1m),
                VoteKind.Reject => -Math.Clamp(vote.Confidence, 0m, 1m),
                _ => 0m,
            });

        // Divided by every voter, faults included. A seat that failed dilutes conviction
        // rather than vanishing, so a room that half broke cannot look unanimous.
        var net = weighted / votes.Count;
        var quorumMet = !requireEveryVoter || faults == 0;
        var approved = quorumMet
            && net > approveThreshold
            && (!requireAnApproval || approvals > 0);

        return new VoteTally
        {
            Approvals = approvals,
            Rejections = rejections,
            Abstentions = abstentions,
            Faults = faults,
            Net = net,
            SizeMultiplier = approved ? Math.Clamp(net, 0m, 1m) : 0m,
            QuorumMet = quorumMet,
            Approved = approved,
        };
    }

    public override string ToString() =>
        $"{Approvals} approve, {Rejections} reject, {Abstentions} abstain, {Faults} faulted; "
        + $"net {Net:F2}, size {SizeDescription}";

    // "size 0%" on a cleared proposal reads as "approved but trades nothing", which is the
    // opposite of what happens: ContractsFor floors an approval at one contract.
    private string SizeDescription =>
        Approved && SizeMultiplier <= 0m ? "minimum" : SizeMultiplier.ToString("P0");
}

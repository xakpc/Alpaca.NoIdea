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

    /// <summary>
    /// Applies the tally to a proposed contract count.
    /// </summary>
    /// <remarks>
    /// A cleared proposal always trades at least one contract: rounding a positive conviction
    /// down to zero would silently turn an approval into a rejection, which is a different
    /// decision wearing the same name.
    /// </remarks>
    public int ContractsFor(int proposed) =>
        SizeMultiplier <= 0m
            ? 0
            : Math.Max(1, (int)Math.Round(proposed * SizeMultiplier, MidpointRounding.AwayFromZero));

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
    public static VoteTally Count(
        IReadOnlyList<PersonaVote> votes,
        decimal approveThreshold = 0m,
        bool requireEveryVoter = true)
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
        var approved = quorumMet && net > approveThreshold;

        return new VoteTally
        {
            Approvals = approvals,
            Rejections = rejections,
            Abstentions = abstentions,
            Faults = faults,
            Net = net,
            SizeMultiplier = approved ? Math.Clamp(net, 0m, 1m) : 0m,
            QuorumMet = quorumMet,
        };
    }

    public override string ToString() =>
        $"{Approvals} approve, {Rejections} reject, {Abstentions} abstain, {Faults} faulted; "
        + $"net {Net:F2}, size {SizeMultiplier:P0}";
}

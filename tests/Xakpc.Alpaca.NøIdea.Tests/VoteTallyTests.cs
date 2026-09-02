using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The arithmetic that decides whether the system ever opens a position.
/// </summary>
/// <remarks>
/// These exist because a negative approve threshold silently could not trade. The verdict was
/// read off <c>SizeMultiplier</c>, which floors at zero, so every proposal cleared on negative
/// conviction was recorded as rejected and sized to nothing. The threshold looked configured
/// and did nothing.
/// </remarks>
public class VoteTallyTests
{
    private const decimal ActivityThreshold = -0.15m;

    private static PersonaVote Vote(string persona, VoteKind kind, decimal confidence) => new()
    {
        Persona = persona,
        Vote = kind,
        Confidence = confidence,
        Rationale = "test",
    };

    private static PersonaVote Abstain(string persona) =>
        Vote(persona, VoteKind.Abstain, 0m);

    /// <summary>Four seats: three abstain, one rejects at the given confidence.</summary>
    private static IReadOnlyList<PersonaVote> OneRejector(decimal confidence) =>
    [
        Vote("skeptic", VoteKind.Reject, confidence),
        Abstain("quant"),
        Abstain("market"),
        Abstain("exposure"),
    ];

    [Theory]
    // Net -0.125. Above the threshold, so the room did not stop it.
    [InlineData(0.50, true, 1)]
    // Net exactly -0.15. The comparison is strict, so equal is not above.
    [InlineData(0.60, false, 0)]
    // Net -0.1875. One confident objection is enough on its own.
    [InlineData(0.75, false, 0)]
    public void OneRejectorDecidesAgainstTheActivityThreshold(
        double confidence, bool approved, int contracts)
    {
        var tally = VoteTally.Count(OneRejector((decimal)confidence), ActivityThreshold);

        Assert.Equal(approved, tally.Approved);
        Assert.Equal(contracts, tally.ContractsFor(1));
    }

    [Fact]
    public void AnApprovedNegativeNetStillTradesTheMinimum()
    {
        var tally = VoteTally.Count(OneRejector(0.50m), ActivityThreshold);

        // The point of the fix: conviction is negative, so there is no size to scale up from,
        // and the trade is still the one the room cleared.
        Assert.True(tally.Approved);
        Assert.Equal(0m, tally.SizeMultiplier);
        Assert.Equal(1, tally.ContractsFor(1));
        Assert.Equal(1, tally.ContractsFor(4));
    }

    [Fact]
    public void AnAbstainingRoomClearsTheActivityThresholdAtMinimumSize()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Abstain("skeptic"), Abstain("quant"), Abstain("market"), Abstain("exposure"),
        ];

        var tally = VoteTally.Count(votes, ActivityThreshold);

        // Deliberate. "Nothing specific is wrong" is the vote the new reviewer standard asks
        // for, and at a negative threshold it opens a position rather than blocking one.
        Assert.True(tally.Approved);
        Assert.Equal(0m, tally.Net);
        Assert.Equal(1, tally.ContractsFor(1));
    }

    [Fact]
    public void TheSameRoomIsRejectedAtTheDefaultThreshold()
    {
        var tally = VoteTally.Count(OneRejector(0.50m));

        Assert.False(tally.Approved);
        Assert.Equal(0, tally.ContractsFor(1));
    }

    [Fact]
    public void AFaultedVoteFailsQuorumHoweverLowTheThreshold()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Approve, 0.90m),
            Vote("quant", VoteKind.Approve, 0.90m),
            Vote("market", VoteKind.Approve, 0.90m),
            PersonaVote.Abstained("exposure", "the seat threw"),
        ];

        var tally = VoteTally.Count(votes, ActivityThreshold);

        Assert.False(tally.QuorumMet);
        Assert.False(tally.Approved);
        Assert.Equal(0, tally.ContractsFor(1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PositiveConvictionSizesTheSameAtEitherThreshold(int proposed)
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Approve, 0.90m),
            Vote("quant", VoteKind.Approve, 0.90m),
            Vote("market", VoteKind.Approve, 0.50m),
            Abstain("exposure"),
        ];

        var atDefault = VoteTally.Count(votes);
        var atActivity = VoteTally.Count(votes, ActivityThreshold);

        // Lowering the bar to clear a proposal must not also change how large it becomes.
        Assert.Equal(atDefault.SizeMultiplier, atActivity.SizeMultiplier);
        Assert.Equal(atDefault.ContractsFor(proposed), atActivity.ContractsFor(proposed));
        Assert.True(atActivity.ContractsFor(proposed) >= 1);
    }

    [Fact]
    public void SizeNeverExceedsWhatTheProposerAskedFor()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Approve, 0.90m),
            Vote("quant", VoteKind.Approve, 0.90m),
            Vote("market", VoteKind.Approve, 0.90m),
            Vote("exposure", VoteKind.Approve, 0.90m),
        ];

        var tally = VoteTally.Count(votes, ActivityThreshold);

        Assert.Equal(2, tally.ContractsFor(2));
        Assert.Equal(1, tally.ContractsFor(1));
    }

    [Fact]
    public void NoVotesApprovesNothing()
    {
        var tally = VoteTally.Count([], ActivityThreshold);

        Assert.False(tally.Approved);
        Assert.False(tally.QuorumMet);
        Assert.Equal(0, tally.ContractsFor(1));
    }

    [Fact]
    public void AClearedProposalWithNoSizeReadsAsMinimumNotZeroPercent()
    {
        var tally = VoteTally.Count(OneRejector(0.50m), ActivityThreshold);

        // "approved, size 0%" in the operator log describes the opposite of what happens.
        Assert.Contains("minimum", tally.ToString(), StringComparison.Ordinal);
    }
}

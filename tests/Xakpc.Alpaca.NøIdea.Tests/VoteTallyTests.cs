using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// The arithmetic that decides whether the system ever opens a position.
/// </summary>
/// <remarks>
/// <para>
/// These exist because a negative approve threshold silently could not trade. The verdict was
/// read off <c>SizeMultiplier</c>, which floors at zero, so every proposal cleared on negative
/// conviction was recorded as rejected and sized to nothing. The threshold looked configured
/// and did nothing.
/// </para>
/// <para>
/// The repair had its own edge: a threshold below zero is cleared by a room that says nothing
/// at all. New money therefore needs an approving seat as well, and the tests below hold both
/// halves in place — the threshold measures conviction against, the approval rule measures
/// whether anybody was for it.
/// </para>
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

    /// <summary>Four seats with no view at all.</summary>
    private static IReadOnlyList<PersonaVote> SilentRoom() =>
    [
        Abstain("skeptic"), Abstain("quant"), Abstain("market"), Abstain("exposure"),
    ];

    [Fact]
    public void AnAbstainingRoomClearsTheThresholdOnItsOwn()
    {
        var tally = VoteTally.Count(SilentRoom(), ActivityThreshold);

        // The threshold answers "how much conviction against is tolerable". Silence carries
        // none, so it passes that question. That is the arithmetic, not the decision.
        Assert.True(tally.Approved);
        Assert.Equal(0m, tally.Net);
        Assert.Equal(0, tally.Approvals);
    }

    [Fact]
    public void ASilentRoomCannotOpenAPosition()
    {
        var tally = VoteTally.Count(SilentRoom(), ActivityThreshold, requireAnApproval: true);

        // On 2026-09-02 this room bought 1,392 USD of META, with two seats stating a profit
        // probability of 0.41 and no seat backing the trade. Nobody's silence spends money.
        Assert.False(tally.Approved);
        Assert.Equal(0, tally.ContractsFor(1));
        Assert.Equal(4, tally.Abstentions);
    }

    [Fact]
    public void OneApprovingSeatIsEnoughToOpen()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Approve, 0.50m),
            Abstain("quant"),
            Abstain("market"),
            Abstain("exposure"),
        ];

        var tally = VoteTally.Count(votes, ActivityThreshold, requireAnApproval: true);

        // The rule asks whether anybody wanted the trade, not for a majority. An abstention
        // still dilutes: net is 0.125, not 0.50.
        Assert.True(tally.Approved);
        Assert.Equal(0.125m, tally.Net);
        Assert.Equal(1, tally.ContractsFor(1));
    }

    [Fact]
    public void AnApprovalDoesNotRescueARoomThatIsAgainstTheTrade()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Approve, 0.50m),
            Vote("quant", VoteKind.Reject, 0.90m),
            Vote("market", VoteKind.Reject, 0.90m),
            Abstain("exposure"),
        ];

        var tally = VoteTally.Count(votes, ActivityThreshold, requireAnApproval: true);

        // Both conditions must hold. The approval satisfies one and the threshold still fails.
        Assert.False(tally.Approved);
        Assert.True(tally.Net < ActivityThreshold);
    }

    [Fact]
    public void AClosingRoomStillDecidesOnConvictionAlone()
    {
        IReadOnlyList<PersonaVote> votes =
        [
            Vote("skeptic", VoteKind.Reject, 0.60m),
            Abstain("quant"),
            Abstain("market"),
            Abstain("exposure"),
        ];

        // A close is judged at a threshold of zero and without the approval rule, because a
        // position must stay easy to leave. Here the room is against closing, so it holds.
        var tally = VoteTally.Count(votes, requireAnApproval: false);

        Assert.False(tally.Approved);
        Assert.Equal(0, tally.Approvals);
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

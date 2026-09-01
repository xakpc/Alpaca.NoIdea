using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Agents.Room.Personas;
using Xakpc.Alpaca.NøIdea.Trading;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>The model family and sampling contract for each LLM seat.</summary>
public class PersonaProviderTests
{
    [Fact]
    public void SeatsUseTheIntendedModelFamilies()
    {
        using var factory = new ChatClientFactory();
        var seats = Seats(factory);

        Assert.Collection(
            seats,
            seat => AssertSeat(seat, "proposer", ModelProvider.Grok),
            seat => AssertSeat(seat, "market", ModelProvider.OpenAi),
            seat => AssertSeat(seat, "quant", ModelProvider.OpenAi),
            seat => AssertSeat(seat, "skeptic", ModelProvider.Anthropic));
    }

    [Fact]
    public void ProfilesResolveTheModelsForTheAssignedSeats()
    {
        Assert.Equal("grok-4.6", ChatModelProfile.Standard.For(ModelProvider.Grok));
        Assert.Equal("gpt-5.6-terra", ChatModelProfile.Standard.For(ModelProvider.OpenAi));
        Assert.Equal("claude-sonnet-5", ChatModelProfile.Standard.For(ModelProvider.Anthropic));

        Assert.Equal("grok-4.6", ChatModelProfile.Cheap.For(ModelProvider.Grok));
        Assert.Equal("gpt-5.4-nano", ChatModelProfile.Cheap.For(ModelProvider.OpenAi));
        Assert.Equal(
            "claude-haiku-4-5-20251001",
            ChatModelProfile.Cheap.For(ModelProvider.Anthropic));
    }

    [Fact]
    public void OpenAiSeatsDoNotSendTemperatureAndGrokProposerDoes()
    {
        using var factory = new ChatClientFactory();
        var seats = Seats(factory);

        Assert.Equal(0.4f, SamplingTemperature(seats[0]));
        Assert.Null(SamplingTemperature(seats[1]));
        Assert.Null(SamplingTemperature(seats[2]));
        Assert.Null(SamplingTemperature(seats[3]));
    }

    private static LlmPersona[] Seats(ChatClientFactory factory) =>
    [
        new ProposerPersona(
            factory, NullLogger.Instance, Array.Empty<AITool>(), new TradingOptions()),
        new MarketPersona(factory, NullLogger.Instance, Array.Empty<AITool>()),
        new QuantPersona(factory, NullLogger.Instance, Array.Empty<AITool>()),
        new SkepticPersona(factory, NullLogger.Instance, Array.Empty<AITool>()),
    ];

    private static void AssertSeat(IPersona seat, string name, ModelProvider provider)
    {
        Assert.Equal(name, seat.Name);
        Assert.Equal(provider, seat.Provider);
    }

    private static float? SamplingTemperature(LlmPersona seat)
    {
        var property = typeof(LlmPersona).GetProperty(
            "SamplingTemperature",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(property);
        return (float?)property.GetValue(seat);
    }
}

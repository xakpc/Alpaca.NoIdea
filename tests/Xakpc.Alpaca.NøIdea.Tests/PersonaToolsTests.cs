using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents.Room;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// What a seat is allowed to call, and who decides it.
/// </summary>
/// <remarks>
/// Both properties here have already failed in a live run. The room could not sit at all
/// because two tools of one name reached Anthropic, and a replay would have reached the open
/// web despite a host that thought it had passed an empty toolbox.
/// </remarks>
public class PersonaToolsTests
{
    /// <summary>A seat that exposes the composed toolset. It never calls a model.</summary>
    private sealed class TestPersona(IReadOnlyList<AITool> alpacaTools, bool webSearchAvailable)
        : LlmPersona(new ChatClientFactory(), NullLogger.Instance, alpacaTools, webSearchAvailable)
    {
        public override string Name => "test";

        public override ModelProvider Provider => ModelProvider.Anthropic;

        protected override string Model => "claude-sonnet-5";

        protected override string RolePrompt => "test";

        public IReadOnlyList<AITool> Tools => ResearchTools;
    }

    private static AITool Named(string name) =>
        AIFunctionFactory.Create(() => "ok", name, name);

    [Fact]
    public void AHostThatSuppliesItsOwnWebSearchIsRefusedAtConstruction()
    {
        // The original failure. The host put a HostedWebSearchTool in the list AND the seat
        // added another, so every request carried two tools named web_search. Anthropic
        // refuses that with "Tool names must be unique": a 400 naming no tool, on every
        // call, so the room never sat and every cycle ended NO_TRADE.
        var error = Assert.Throws<ArgumentException>(
            () => new TestPersona([Named("get_news"), new HostedWebSearchTool()], true));

        Assert.Contains("HostedWebSearchTool", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoToolNameIsOfferedTwice()
    {
        var persona = new TestPersona([Named("get_stock_bars"), Named("get_news")], true);

        var names = persona.Tools.Select(tool => tool.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AHostThatOffersNoWebSearchGetsNone()
    {
        // The replay guarantee. A seat asks for web search by default, and only the host
        // knows that a historical run must reach nothing live: a search made now returns
        // everything published since the replay instant.
        var persona = new TestPersona([], webSearchAvailable: false);

        Assert.Empty(persona.Tools);
    }

    [Fact]
    public void AHostThatOffersWebSearchGetsIt()
    {
        var persona = new TestPersona([], webSearchAvailable: true);

        Assert.Single(persona.Tools);
        Assert.IsType<HostedWebSearchTool>(persona.Tools[0]);
    }
}

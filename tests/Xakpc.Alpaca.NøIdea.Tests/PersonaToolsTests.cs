using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Research;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// What a seat is allowed to call, and who decides it.
/// </summary>
/// <remarks>
/// The rule is one line: <b>the host's list is the seat's toolset</b>. A seat that builds a
/// tool of its own has broken a live run twice — once with two tools of one name, which
/// Anthropic refuses outright, and once by reaching the open web from a replay that thought
/// it had passed an empty toolbox.
/// </remarks>
public class PersonaToolsTests
{
    /// <summary>A seat that exposes the composed toolset. It never calls a model.</summary>
    private sealed class TestPersona(IReadOnlyList<AITool> researchTools)
        : LlmPersona(new ChatClientFactory(), NullLogger.Instance, researchTools)
    {
        public override string Name => "test";

        public override ModelProvider Provider => ModelProvider.OpenAi;

        protected override string Model => "gpt-5.6-terra";

        protected override string RolePrompt => "test";

        public IReadOnlyList<AITool> Tools => ResearchTools;
    }

    private static AITool Named(string name) =>
        AIFunctionFactory.Create(() => "ok", name, name);

    [Fact]
    public void AHostThatOffersNothingGetsASeatWithNothing()
    {
        // The replay guarantee, and the whole of it. A live Alpaca call reads today's market
        // and a web search returns everything published since the replay instant, so a
        // historical run that reaches either looks brilliant for the wrong reason.
        var persona = new TestPersona([]);

        Assert.Empty(persona.Tools);
    }

    [Fact]
    public void ASeatAddsNoToolOfItsOwn()
    {
        var offered = new[] { Named("get_stock_bars"), Named("search_web_pages") };

        var persona = new TestPersona(offered);

        Assert.Equal(
            offered.Select(tool => tool.Name),
            persona.Tools.Select(tool => tool.Name));
    }

    [Fact]
    public void NoToolNameIsOfferedTwice()
    {
        // Anthropic refuses a repeated name with "Tool names must be unique": a 400 that
        // names no tool, on every call, so the room never sits and every cycle reads as a
        // considered NO_TRADE.
        var persona = new TestPersona([Named("get_stock_bars"), Named("get_news")]);

        var names = persona.Tools.Select(tool => tool.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OnlyTheApprovedWebToolsAreEverOffered()
    {
        // An allowlist, not whatever the server exposes. A server that grows a tool later
        // must not reach a model because nobody looked (ADR-005, ADR-006).
        Assert.Equal(
            ["fetch_page_content", "search_web_pages"],
            KeenableMcpClient.ApprovedTools.Order(StringComparer.Ordinal));
    }
}

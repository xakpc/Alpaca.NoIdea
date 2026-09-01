using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Observability;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// What the console shows of a seat's conversation with its model.
/// </summary>
/// <remarks>
/// The record used to hold only what a seat submitted, so a proposer that spent 341,000 input
/// tokens over a dozen tool calls and then said NO_TRADE read exactly like one that looked at
/// nothing. These tests hold the two properties that make the record worth keeping: the tool
/// calls and their arguments are in it, and each seat owns its own event ids.
/// </remarks>
public class ChatTranscriptTests
{
    /// <summary>A logger that keeps every line, so a test can read the record back.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(EventId Id, LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((eventId, logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// A model that answers from a script, and refuses to stream.
    /// </summary>
    /// <remarks>
    /// The refusal is the point of the type as much as the script: every call this
    /// application makes is <c>GetResponseAsync</c>, and a change to a streamed call would
    /// fail here rather than in a live cycle.
    /// </remarks>
    private sealed class ScriptedChatClient(params ChatResponse[] turns) : IChatClient
    {
        private int _turn;

        public int Calls => _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(turns[Math.Min(_turn++, turns.Length - 1)]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This application never streams a turn.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task TheToolLoopLeavesEveryCallAndAnswerInTheResponse()
    {
        // The whole transcript rests on this: the function-calling client must hand back the
        // full loop, not only the last turn. If a package version stops doing that, the log
        // goes quiet about tool use and nothing else reports it.
        var tool = AIFunctionFactory.Create(
            (string symbol) => $"{symbol} closed at 182.40",
            "get_stock_bars",
            "Reads bars.");

        var client = new FunctionInvokingChatClient(new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-1", "get_stock_bars",
                    new Dictionary<string, object?> { ["symbol"] = "NVDA" }),
            ])),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "NVDA looks strong."))));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Look at NVDA.")],
            new ChatOptions { Tools = [tool] });

        var contents = response.Messages.SelectMany(message => message.Contents).ToList();

        var call = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("get_stock_bars", call.Name);
        Assert.Equal("NVDA", call.Arguments?["symbol"]?.ToString());

        Assert.Single(contents.OfType<FunctionResultContent>());
    }

    [Fact]
    public void TheRecordNamesEveryToolCalledAndTheArgumentsItGot()
    {
        var logger = new CapturingLogger();

        ChatTranscript.Response(
            logger, "proposer", "search", "claude-sonnet-5",
            new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "get_stock_bars",
                        new Dictionary<string, object?> { ["symbol"] = "NVDA", ["limit"] = 5 }),
                    new FunctionCallContent("call-2", "search_web_pages",
                        new Dictionary<string, object?> { ["query"] = "NVDA earnings" }),
                ]),
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("call-1", "NVDA closed at 182.40"),
                ]),
                new ChatMessage(ChatRole.Assistant, "I propose one call on NVDA."),
            ])
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = new UsageDetails { InputTokenCount = 158_000, OutputTokenCount = 7_200 },
            },
            TimeSpan.FromSeconds(12));

        var record = string.Join("\n", logger.Lines.Select(line => line.Message));

        // The arguments, not only the tool name: which symbol was read is what explains a
        // proposal afterwards.
        Assert.Contains("get_stock_bars", record, StringComparison.Ordinal);
        Assert.Contains("NVDA", record, StringComparison.Ordinal);
        Assert.Contains("search_web_pages", record, StringComparison.Ordinal);
        Assert.Contains("NVDA earnings", record, StringComparison.Ordinal);

        // What the tool answered, and what the model wrote.
        Assert.Contains("NVDA closed at 182.40", record, StringComparison.Ordinal);
        Assert.Contains("I propose one call on NVDA.", record, StringComparison.Ordinal);

        // And the tally, which is the line a person reads when the room says NO_TRADE.
        Assert.Contains("2 tool calls", record, StringComparison.Ordinal);
        Assert.Contains("158000 in", record, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineBeforeTheCallSaysWhatWhereAndHow()
    {
        // A seat holds the console for minutes. This one line is all a person has during that
        // wait, so it must carry the whole call on its own and stay on its own event id: the
        // prompt dump under the Request id is tens of kilobytes and would bury it.
        var logger = new CapturingLogger();

        ChatTranscript.Sending(
            logger, "quant", "vote", "gpt-5.6-terra", "PR-4831",
            [
                new ChatMessage(ChatRole.System, "You are the quant seat."),
                new ChatMessage(ChatRole.User, "proposal_id: PR-4831"),
            ],
            [AIFunctionFactory.Create(() => "ok", "cast_vote", "Votes.")],
            ChatToolMode.RequireSpecific("cast_vote"),
            1500,
            0.4f);

        var line = Assert.Single(logger.Lines);

        Assert.Equal(RunEvents.Chat("quant", ChatEvent.Sending), line.Id);
        Assert.NotEqual(RunEvents.Chat("quant", ChatEvent.Request), line.Id);

        // What: the seat, the phase, the proposal it belongs to.
        Assert.Contains("quant [vote]", line.Message, StringComparison.Ordinal);
        Assert.Contains("PR-4831", line.Message, StringComparison.Ordinal);

        // Where: the model that is about to be billed for the wait.
        Assert.Contains("gpt-5.6-terra", line.Message, StringComparison.Ordinal);

        // How: the size of the payload, the toolbox, what the seat must do with it, and the
        // sampling settings.
        Assert.Contains("2 messages", line.Message, StringComparison.Ordinal);
        Assert.Contains("1 tools [cast_vote]", line.Message, StringComparison.Ordinal);
        Assert.Contains("must call cast_vote", line.Message, StringComparison.Ordinal);
        Assert.Contains("max 1500 output tokens", line.Message, StringComparison.Ordinal);
        Assert.Contains("temperature 0.40", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeatThatSendsNoTemperatureSaysSo()
    {
        // A reasoning model that answers 400 to `temperature` is an abstention, so the line
        // must show that the value was left unset rather than print a default that was never
        // sent.
        var logger = new CapturingLogger();

        ChatTranscript.Sending(
            logger, "quant", "analysis", "gpt-5.6-terra", "PR-4831",
            [new ChatMessage(ChatRole.User, "payload")],
            [],
            ChatToolMode.Auto,
            3000,
            temperature: null);

        var line = Assert.Single(logger.Lines);

        Assert.Contains("temperature unset", line.Message, StringComparison.Ordinal);
        Assert.Contains("0 tools [none]", line.Message, StringComparison.Ordinal);
        Assert.Contains("tools optional", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachSeatOwnsItsOwnEventIds()
    {
        // A block per seat is what makes "show me only the proposer" a filter and not a
        // search through five conversations that all look alike.
        var seats = new[] { "proposer", "quant", "skeptic", "market", "exposure" };

        var ids = seats
            .SelectMany(seat => Enum.GetValues<ChatEvent>().Select(kind => RunEvents.Chat(seat, kind)))
            .ToList();

        Assert.Equal(ids.Count, ids.Select(id => id.Id).Distinct().Count());
        Assert.All(ids, id => Assert.InRange(id.Id, 4100, 4999));
    }

    [Fact]
    public void TheLastDigitIsTheKindOfLineForEverySeat()
    {
        // One filter selects one seat (41xx), another selects one kind across every seat (an
        // id that ends in 3 is a tool call). Both only work while this holds.
        foreach (var seat in new[] { "proposer", "quant", "skeptic", "market", "exposure" })
        {
            foreach (var kind in Enum.GetValues<ChatEvent>())
            {
                Assert.Equal((int)kind, RunEvents.Chat(seat, kind).Id % 10);
                Assert.Equal($"{seat}.{kind}", RunEvents.Chat(seat, kind).Name);
            }
        }
    }

    [Fact]
    public void ASeatTheTableDoesNotKnowStillGetsALine()
    {
        // A new seat must not take the numbering of an existing one, and must not throw
        // during a trading cycle either. It shares the unknown block until it is added.
        var id = RunEvents.Chat("historian", ChatEvent.ToolCall);

        Assert.Equal(4903, id.Id);
        Assert.Equal("historian.ToolCall", id.Name);
    }
}

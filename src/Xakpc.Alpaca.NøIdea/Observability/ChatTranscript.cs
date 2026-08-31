using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Xakpc.Alpaca.NøIdea.Observability;

/// <summary>
/// Writes the whole conversation between one seat and its model to the log.
/// </summary>
/// <remarks>
/// <para>
/// The console is the record of a run (ADR-024), and the record used to stop at what a seat
/// finally submitted. That is the smallest part of what happened: the proposer can spend three
/// hundred thousand input tokens over a dozen tool calls and then submit NO_TRADE, and the old
/// log showed only the two words. <b>This writes the turns, the tool calls with their
/// arguments, what each tool answered, and the tally at the end.</b>
/// </para>
/// <para>
/// Every line carries the seat's own event id, so one seat's conversation is selectable on its
/// own. See <see cref="RunEvents.Chat"/>.
/// </para>
/// <para>
/// Long text is clipped to <see cref="MaxCharacters"/> and the full length is printed with it.
/// A payload of candidates and headlines is tens of kilobytes, and a console that a person must
/// read during a four-day contest is worth more than a verbatim copy of a document this code
/// built itself.
/// </para>
/// </remarks>
public static class ChatTranscript
{
    /// <summary>How much of one message reaches the log before it is clipped.</summary>
    public const int MaxCharacters = 4000;

    /// <summary>How much of one tool answer reaches the log. Tool output is untrusted.</summary>
    public const int MaxToolResultCharacters = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Writes what the host is about to send.</summary>
    public static void Request(
        ILogger logger,
        string persona,
        string phase,
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(messages);

        var id = RunEvents.Chat(persona, ChatEvent.Request);

        logger.LogInformation(
            id,
            "{Persona} [{Phase}] asks {Model}: {Messages} messages, {Tools} tools [{ToolNames}], "
            + "max {MaxOutputTokens} output tokens.",
            persona, phase, model, messages.Count, tools?.Count ?? 0,
            string.Join(", ", (tools ?? []).Select(tool => tool.Name)),
            maxOutputTokens);

        foreach (var message in messages)
        {
            logger.LogInformation(
                id,
                "{Persona} [{Phase}] sent {Role}: {Text}",
                persona, phase, message.Role.Value, Clip(message.Text, MaxCharacters));
        }
    }

    /// <summary>
    /// Writes everything the model did in answer: its prose, every tool call with the arguments
    /// it passed, every tool answer, and the tally.
    /// </summary>
    /// <remarks>
    /// The turns are read from <see cref="ChatResponse.Messages"/>, which the function-calling
    /// client fills with the whole loop and not only the last turn. A provider that reports
    /// less is under-reported here rather than throwing: a thin record must never stop a
    /// trading cycle.
    /// </remarks>
    public static void Response(
        ILogger logger,
        string persona,
        string phase,
        string model,
        ChatResponse? response,
        TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (response is null)
        {
            logger.LogWarning(
                RunEvents.Chat(persona, ChatEvent.Finished),
                "{Persona} [{Phase}] on {Model} returned nothing after {Seconds:F1}s.",
                persona, phase, model, took.TotalSeconds);
            return;
        }

        var said = RunEvents.Chat(persona, ChatEvent.Said);
        var calling = RunEvents.Chat(persona, ChatEvent.ToolCall);
        var answered = RunEvents.Chat(persona, ChatEvent.ToolResult);

        // Kept so an answer, which carries only the call id, can name the tool it came from.
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        var turns = 0;

        foreach (var message in response.Messages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                turns++;
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        logger.LogInformation(
                            said, "{Persona} [{Phase}] said: {Text}",
                            persona, phase, Clip(text.Text, MaxCharacters));
                        break;

                    case TextReasoningContent reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        logger.LogInformation(
                            said, "{Persona} [{Phase}] reasoned: {Text}",
                            persona, phase, Clip(reasoning.Text, MaxCharacters));
                        break;

                    case FunctionCallContent call:
                        toolNames[call.CallId] = call.Name;
                        tally[call.Name] = tally.GetValueOrDefault(call.Name) + 1;

                        logger.LogInformation(
                            calling, "{Persona} [{Phase}] called {Tool}({Arguments})",
                            persona, phase, call.Name, Describe(call.Arguments));
                        break;

                    case FunctionResultContent result:
                        logger.LogInformation(
                            answered, "{Persona} [{Phase}] {Tool} answered: {Result}",
                            persona, phase,
                            toolNames.GetValueOrDefault(result.CallId, "a tool"),
                            Clip(Describe(result.Result), MaxToolResultCharacters));
                        break;

                    case ErrorContent error:
                        logger.LogWarning(
                            said, "{Persona} [{Phase}] returned an error block: {Message}",
                            persona, phase, error.Message);
                        break;
                }
            }
        }

        var usage = response.Usage;

        logger.LogInformation(
            RunEvents.Chat(persona, ChatEvent.Finished),
            "{Persona} [{Phase}] finished on {Model}: {Reason}, {Turns} turns, "
            + "{ToolCalls} tool calls [{Tools}], {Input} in ({Cached} cached) / {Output} out, "
            + "{Seconds:F1}s.",
            persona, phase, model,
            response.FinishReason?.ToString() ?? "no reason reported",
            turns,
            tally.Values.Sum(),
            tally.Count == 0
                ? "none"
                : string.Join(", ", tally.Select(entry =>
                    entry.Value == 1 ? entry.Key : $"{entry.Key} x{entry.Value}")),
            usage?.InputTokenCount ?? 0,
            usage?.CachedInputTokenCount ?? 0,
            usage?.OutputTokenCount ?? 0,
            took.TotalSeconds);
    }

    /// <summary>Writes a call that threw, under the same seat's ids.</summary>
    public static void Failed(
        ILogger logger,
        string persona,
        string phase,
        string model,
        Exception error,
        TimeSpan took)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(error);

        logger.LogError(
            RunEvents.Chat(persona, ChatEvent.Finished),
            error,
            "{Persona} [{Phase}] failed on {Model} after {Seconds:F1}s: {Message}",
            persona, phase, model, took.TotalSeconds, error.Message);
    }

    /// <summary>
    /// Renders a tool argument or an answer as JSON, and never throws while doing it.
    /// </summary>
    /// <remarks>
    /// The value comes from a model or from an MCP server, so it can hold anything. A log
    /// writer that throws on a strange shape would take the trading cycle down with it.
    /// </remarks>
    private static string Describe(object? value)
    {
        if (value is null)
        {
            return "none";
        }

        if (value is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(value, Json);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? "none";
        }
        catch (JsonException)
        {
            return value.ToString() ?? "none";
        }
    }

    private static string Clip(string? text, int limit) =>
        string.IsNullOrWhiteSpace(text) ? "(nothing)"
        : text.Length <= limit ? text
        : $"{text[..limit]}… ({text.Length:N0} characters in total)";
}

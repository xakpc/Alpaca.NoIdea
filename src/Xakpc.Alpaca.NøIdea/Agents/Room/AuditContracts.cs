using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>A durable audit write failed and the live session must stop.</summary>
public sealed class AuditPersistenceException(string message, Exception innerException)
    : Exception(message, innerException);

/// <summary>One paired model tool request and result.</summary>
public sealed record AgentToolCallAudit(
    string Persona,
    string Phase,
    string Model,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    string? ResultJson,
    string Status);

/// <summary>The storage boundary used by the war room and model call path.</summary>
public interface IWarRoomAuditSink
{
    Task BeginSittingAsync(
        string proposalId,
        string mode,
        WarRoomPurpose purpose,
        long startedUtc,
        CancellationToken cancellationToken);

    Task RecordToolCallsAsync(
        string proposalId,
        IReadOnlyCollection<AgentToolCallAudit> calls,
        CancellationToken cancellationToken);

    Task CompleteSittingAsync(
        string proposalId,
        WarRoomVerdict verdict,
        IReadOnlyCollection<ProposalReviewPass> passes,
        long completedUtc,
        CancellationToken cancellationToken);
}

/// <summary>An explicit test-only sink for rooms that do not use durable storage.</summary>
public sealed class NullWarRoomAuditSink : IWarRoomAuditSink
{
    public static NullWarRoomAuditSink Instance { get; } = new();

    public Task BeginSittingAsync(
        string proposalId, string mode, WarRoomPurpose purpose, long startedUtc,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordToolCallsAsync(
        string proposalId, IReadOnlyCollection<AgentToolCallAudit> calls,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteSittingAsync(
        string proposalId, WarRoomVerdict verdict, IReadOnlyCollection<ProposalReviewPass> passes,
        long completedUtc, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class AgentToolCallCapture
{
    public static IReadOnlyList<AgentToolCallAudit> FromResponse(
        string persona,
        string phase,
        string model,
        ChatResponse? response)
    {
        if (response is null)
        {
            return [];
        }

        var calls = new Dictionary<string, (string Name, string Arguments)>(StringComparer.Ordinal);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var content in response.Messages.SelectMany(message => message.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call:
                    calls[call.CallId] = (call.Name, Serialize(call.Arguments));
                    break;
                case FunctionResultContent result:
                    results[result.CallId] = Serialize(result.Result);
                    break;
            }
        }

        return
        [
            .. calls.Select(pair => new AgentToolCallAudit(
                persona,
                phase,
                model,
                pair.Key,
                pair.Value.Name,
                pair.Value.Arguments,
                results.GetValueOrDefault(pair.Key),
                results.ContainsKey(pair.Key) ? "completed" : "missing-result")),
        ];
    }

    private static string Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException)
        {
            return JsonSerializer.Serialize(value?.ToString());
        }
    }
}

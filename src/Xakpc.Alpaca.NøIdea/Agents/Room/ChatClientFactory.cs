using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Xakpc.Alpaca.NøIdea.Alpaca;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

/// <summary>The provider model IDs for one war-room cost profile.</summary>
public sealed record ChatModelProfile(string Anthropic, string OpenAi, string Grok)
{
    public static ChatModelProfile Standard { get; } =
        new("claude-sonnet-5", "gpt-5.6-terra", "grok-4.6");

    public static ChatModelProfile Cheap { get; } =
        new("claude-haiku-4-5-20251001", "gpt-5.4-nano", "grok-4.6");

    public string For(ModelProvider provider) => provider switch
    {
        ModelProvider.Anthropic => Anthropic,
        ModelProvider.OpenAi => OpenAi,
        ModelProvider.Grok => Grok,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No model for this provider."),
    };
}

/// <summary>
/// Builds an <see cref="IChatClient"/> for a persona's chosen provider.
/// </summary>
/// <remarks>
/// <para>
/// Every provider ends up behind one <see cref="IChatClient"/>, so the room, the tool
/// definitions and the vote handling are written once and work for all of them. That is what
/// makes a mixed room cheap to build, and a mixed room is the point: independent errors are
/// what make a second opinion worth its tokens.
/// </para>
/// <para>
/// Clients are cached per provider, so five personas on one provider open one client.
/// </para>
/// </remarks>
public sealed class ChatClientFactory : IDisposable
{
    /// <summary>The xAI endpoint. Grok speaks the OpenAI protocol.</summary>
    private static readonly Uri GrokEndpoint = new("https://api.x.ai/v1");

    /// <summary>How long one HTTP request to Grok may take before the transport kills it.</summary>
    /// <remarks>
    /// A reasoning turn is one HTTP request, and the tool loop makes one request per turn, so
    /// this is a per-turn limit and not a per-call limit. Grok turns in the search phase carry
    /// 300,000 to 400,000 input tokens. Measured healthy turns reach about 180 seconds, and one
    /// measured sitting averaged 143 seconds across five turns. Four minutes admits the slow but
    /// healthy turn and still kills a dead socket well inside the seat's own limit.
    /// </remarks>
    private static readonly TimeSpan GrokRequestTimeout = TimeSpan.FromMinutes(4);

    /// <summary>How long one HTTP request to OpenAI may take before the transport kills it.</summary>
    /// <remarks>OpenAI turns measure between 10 and 100 seconds, so it needs less room than Grok.</remarks>
    private static readonly TimeSpan OpenAiRequestTimeout = TimeSpan.FromMinutes(3);

    private readonly Dictionary<ModelProvider, IChatClient> _clients = [];
    private readonly Lock _gate = new();
    private readonly ChatModelProfile _models;

    public ChatClientFactory(ChatModelProfile? models = null)
    {
        _models = models ?? ChatModelProfile.Standard;
    }

    public string ModelFor(ModelProvider provider) => _models.For(provider);

    /// <summary>The environment variable a provider reads its key from.</summary>
    public static string KeyVariable(ModelProvider provider) => provider switch
    {
        ModelProvider.Anthropic => "ANTHROPIC_API_KEY",
        ModelProvider.OpenAi => "OPENAI_API_KEY",
        ModelProvider.Grok => "XAI_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "No key for this provider."),
    };

    /// <summary>
    /// Names every provider a persona needs but has no key for.
    /// </summary>
    /// <remarks>
    /// Deliberately eager, and called before the first cycle. A missing key found mid-cycle is
    /// a dead seat at 09:31 on the first trading morning; found at startup it is a one-line
    /// fix before the open.
    /// </remarks>
    public static IReadOnlyList<string> MissingKeys(IEnumerable<IPersona> personas)
    {
        ArgumentNullException.ThrowIfNull(personas);

        return personas
            .Select(persona => persona.Provider)
            .Where(provider => provider != ModelProvider.None)
            .Distinct()
            .Select(KeyVariable)
            .Where(variable => string.IsNullOrWhiteSpace(AlpacaOptions.Secret(variable)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The transport settings for one OpenAI-protocol host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK defaults are wrong for a reasoning model that invokes tools. They give one HTTP
    /// request 100 seconds and then retry it three times, and they count a slow generation as a
    /// retryable fault. A healthy Grok search turn is longer than 100 seconds, so the default
    /// cancels work that would have completed, re-sends the same 400,000-token prompt, and is
    /// billed for each attempt. The seat then reports a fault after nine minutes, the room loses
    /// the cycle, and the token ledger sees no response and records nothing.
    /// </para>
    /// <para>
    /// One retry keeps the answer to a rate limit or a bad gateway, which is what a retry is for.
    /// It removes the retry of a slow generation, which no number of attempts can make faster.
    /// The seat's own <c>CallTimeout</c> stays the outer limit, as it is written to be.
    /// </para>
    /// </remarks>
    private static OpenAIClientOptions Transport(TimeSpan requestTimeout, Uri? endpoint = null)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = requestTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
        };

        if (endpoint is not null)
        {
            options.Endpoint = endpoint;
        }

        return options;
    }

    public IChatClient For(ModelProvider provider, string personaName)
    {
        if (provider == ModelProvider.None)
        {
            throw new InvalidOperationException(
                $"{personaName} declares no provider, so it must not ask for a chat client.");
        }

        lock (_gate)
        {
            if (_clients.TryGetValue(provider, out var existing))
            {
                return existing;
            }

            var variable = KeyVariable(provider);
            var key = AlpacaOptions.Secret(variable)
                      ?? throw new InvalidOperationException(
                          $"{variable} is not set. {personaName} needs it. Add it to .env.");

            IChatClient created = provider switch
            {
                ModelProvider.Anthropic =>
                    new Anthropic.AnthropicClient { ApiKey = key }.AsIChatClient(),

                ModelProvider.OpenAi =>
#pragma warning disable OPENAI001 // Responses API is required for reasoning models that invoke tools.
                    new OpenAIClient(
                            new ApiKeyCredential(key),
                            Transport(OpenAiRequestTimeout))
                        .GetResponsesClient()
                        .AsIChatClient(_models.OpenAi),
#pragma warning restore OPENAI001

                // Same protocol, different host. The model id still comes from the persona
                // through ChatOptions, so this default is only a constructor argument.
                ModelProvider.Grok =>
                    new OpenAIClient(
                            new ApiKeyCredential(key),
                            Transport(GrokRequestTimeout, GrokEndpoint))
                        .GetChatClient(_models.Grok)
                        .AsIChatClient(),

                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };

            // The tool loop is provider-independent, so it is wrapped once here rather than
            // in every persona.
            //
            // The iteration cap bounds spend, which the per-call timeout does not: a fast model
            // stuck in a tool loop burns tokens without burning wall-clock. The default is 40;
            // the busiest measured search used 20, so 25 guards the pathological case without
            // truncating observed work.
            var wrapped = new FunctionInvokingChatClient(created)
            {
                MaximumIterationsPerRequest = 25,
            };
            _clients[provider] = wrapped;
            return wrapped;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }
}

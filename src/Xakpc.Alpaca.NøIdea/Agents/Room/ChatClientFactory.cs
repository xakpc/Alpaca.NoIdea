using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Xakpc.Alpaca.NøIdea.Alpaca;

namespace Xakpc.Alpaca.NøIdea.Agents.Room;

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

    private readonly Dictionary<ModelProvider, IChatClient> _clients = [];
    private readonly Lock _gate = new();

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
                    new Anthropic.SDK.AnthropicClient(key).Messages,

                ModelProvider.OpenAi =>
                    new OpenAIClient(new ApiKeyCredential(key))
                        .GetChatClient("gpt-5")
                        .AsIChatClient(),

                // Same protocol, different host. The model id still comes from the persona
                // through ChatOptions, so this default is only a constructor argument.
                ModelProvider.Grok =>
                    new OpenAIClient(
                            new ApiKeyCredential(key),
                            new OpenAIClientOptions { Endpoint = GrokEndpoint })
                        .GetChatClient("grok-4")
                        .AsIChatClient(),

                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };

            // The tool loop is provider-independent, so it is wrapped once here rather than
            // in every persona.
            var wrapped = new FunctionInvokingChatClient(created);
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

using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace RefundDesk.Infrastructure;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Tolerant parse: accepts a bare object or one wrapped in a ```json fence.</summary>
    public static T? ParseLoose<T>(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return default;
        try { return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Options); }
        catch (JsonException) { return default; }
    }
}

public sealed class LlmSettings
{
    /// <summary>auto | anthropic | openai | scripted</summary>
    public string Provider { get; set; } = "auto";
    public string? ApiKey { get; set; }
    /// <summary>OpenAI-compatible base URL (OpenAI, Azure OpenAI v1, Ollama, vLLM, OpenRouter...).</summary>
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public int MaxOutputTokens { get; set; } = 8000;
    /// <summary>Use the provider's JSON-schema response format. Set false for endpoints that lack it.</summary>
    public bool UseJsonSchema { get; set; } = true;
    /// <summary>Simulated think time per call in scripted mode, so the replay reads like a live run.</summary>
    public int ScriptedDelayMs { get; set; } = 900;
}

public static class LlmFactory
{
    public static (IChatClient Client, string Label, bool IsScripted) Create(LlmSettings s)
    {
        var provider = s.Provider.Trim().ToLowerInvariant();
        s.ApiKey ??= provider is "openai"
            ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (provider == "auto")
            provider = string.IsNullOrWhiteSpace(s.ApiKey) ? "scripted"
                     : string.IsNullOrWhiteSpace(s.Endpoint) ? "anthropic" : "openai";

        switch (provider)
        {
            case "anthropic":
            {
                var model = s.Model ?? "claude-opus-5-5";
                var client = new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = s.ApiKey });
                return (client.AsIChatClient(model), $"Anthropic · {model}", false);
            }
            case "openai":
            {
                var model = s.Model ?? throw new InvalidOperationException("Llm:Model is required for the openai provider.");
                var options = new OpenAIClientOptions();
                if (!string.IsNullOrWhiteSpace(s.Endpoint)) options.Endpoint = new Uri(s.Endpoint);
                var client = new OpenAIClient(new ApiKeyCredential(s.ApiKey ?? "unused"), options);
                return (client.GetChatClient(model).AsIChatClient(), $"OpenAI-compatible · {model} @ {s.Endpoint ?? "api.openai.com"}", false);
            }
            case "scripted":
                return (new ScriptedChatClient(s.ScriptedDelayMs), "Scripted replay (offline, no model)", true);
            default:
                throw new InvalidOperationException($"Unknown Llm:Provider '{s.Provider}'.");
        }
    }
}

/// <summary>
/// Names the pipeline stage that is currently running. Flows through async calls, so
/// the meter can attribute cost per stage, and parallel workers each get their own name.
/// </summary>
public static class Stage
{
    private static readonly AsyncLocal<string?> CurrentName = new();
    public static string Current => CurrentName.Value ?? "unscoped";

    public static IDisposable Enter(string name)
    {
        var previous = CurrentName.Value;
        CurrentName.Value = name;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentName.Value = previous;
    }
}

/// <summary>Economics per stage: slide 15 "track per successful task".</summary>
public sealed class Meter
{
    public sealed class StageStats
    {
        public int ModelCalls;
        public long InputTokens;
        public long OutputTokens;
        public int ToolCalls;
        public long ElapsedMs;
    }

    public ConcurrentDictionary<string, StageStats> Stages { get; } = new();
    public int EvaluatorRounds;
    public int HumanInterventions;
    public int EffectRetries;
    public int LoopIterations;

    private StageStats For(string stage) => Stages.GetOrAdd(stage.Split(':')[0], _ => new StageStats());

    public void ModelCall(string stage, UsageDetails? usage, TimeSpan elapsed)
    {
        var s = For(stage);
        Interlocked.Increment(ref s.ModelCalls);
        Interlocked.Add(ref s.InputTokens, usage?.InputTokenCount ?? 0);
        Interlocked.Add(ref s.OutputTokens, usage?.OutputTokenCount ?? 0);
        Interlocked.Add(ref s.ElapsedMs, (long)elapsed.TotalMilliseconds);
    }

    public void ToolCall(string stage) => Interlocked.Increment(ref For(stage).ToolCalls);
}

/// <summary>Middleware in the IChatClient pipeline: every model call is counted and timed.</summary>
public sealed class MeteredChatClient(IChatClient inner, Meter meter) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        meter.ModelCall(Stage.Current, response.Usage, sw.Elapsed);
        return response;
    }
}

/// <summary>One structured-output call: system + user in, typed record out.</summary>
public sealed class ModelGateway(IChatClient chat, LlmSettings settings)
{
    public IChatClient Chat => chat;
    public LlmSettings Settings => settings;

    public async Task<T> AskAsync<T>(string system, string user, CancellationToken ct = default) where T : class
    {
        List<ChatMessage> messages = [new(ChatRole.System, system), new(ChatRole.User, user)];
        var options = new ChatOptions { MaxOutputTokens = settings.MaxOutputTokens };
        var response = await chat.GetResponseAsync<T>(messages, Json.Options, options, settings.UseJsonSchema, ct);

        if (response.TryGetResult(out var result) && result is not null)
            return result;
        return Json.ParseLoose<T>(response.Text)
            ?? throw new InvalidOperationException($"Model did not return a valid {typeof(T).Name}:\n{response.Text}");
    }
}

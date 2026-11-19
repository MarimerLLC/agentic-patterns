using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace RefundDesk.Infrastructure;

public sealed class DecisionSettings
{
    /// <summary>Foundry deployment name of Microsoft-Decision-1. Empty = the router uses the LLM.</summary>
    public string? Deployment { get; set; }
    /// <summary>Foundry resource URL. Defaults to the Llm:Endpoint resource.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Defaults to Llm:ApiKey (same Foundry resource).</summary>
    public string? ApiKey { get; set; }
}

public sealed record DecisionAnswer(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities, string Model);

/// <summary>
/// Microsoft-Decision-1 in Azure AI Foundry: a small model that picks one of a fixed set of
/// options and returns CALIBRATED probabilities, instead of generating text. It is not an
/// IChatClient (different API: POST {resource}/providers/microsoft/v1/systemone), so it gets
/// its own thin client, metered and traced like every other model call.
/// </summary>
public sealed class DecisionClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Uri _url;
    private readonly Meter _meter;

    public string Deployment { get; }
    public string Host => _url.Host;

    private DecisionClient(Uri url, string deployment, string apiKey, Meter meter)
    {
        _url = url;
        _meter = meter;
        Deployment = deployment;
        _http.DefaultRequestHeaders.Add("api-key", apiKey);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>Null when no deployment is configured; the router then uses the LLM.</summary>
    public static DecisionClient? Create(DecisionSettings s, LlmSettings llm, Meter meter)
    {
        if (string.IsNullOrWhiteSpace(s.Deployment)) return null;
        var endpoint = s.Endpoint ?? llm.Endpoint
            ?? throw new InvalidOperationException("Decision:Endpoint (or Llm:Endpoint) is required for Decision:Deployment.");
        var key = s.ApiKey ?? llm.ApiKey
            ?? throw new InvalidOperationException("Decision:ApiKey (or Llm:ApiKey) is required for Decision:Deployment.");
        return new DecisionClient(UrlFor(endpoint), s.Deployment, key, meter);
    }

    /// <summary>The decision API lives on the Foundry host (*.services.ai.azure.com) of the same resource.</summary>
    public static Uri UrlFor(string endpoint)
    {
        var host = new Uri(endpoint.Trim()).Host;
        foreach (var suffix in new[] { ".openai.azure.com", ".cognitiveservices.azure.com" })
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                host = host[..^suffix.Length] + ".services.ai.azure.com";
        return new Uri($"https://{host}/providers/microsoft/v1/systemone");
    }

    /// <summary>One "choice" question: pick exactly one of <paramref name="options"/> (name → description).</summary>
    public async Task<DecisionAnswer> ChooseAsync(
        string question, string instructions, string state, IReadOnlyDictionary<string, string> options, CancellationToken ct = default)
    {
        using var span = Telemetry.Source.StartActivity($"choice {Deployment}", ActivityKind.Client);
        span?.SetTag("gen_ai.operation.name", "choice");
        span?.SetTag("gen_ai.provider.name", "azure.ai.foundry");
        span?.SetTag("gen_ai.request.model", Deployment);
        if (Telemetry.CaptureContent) span?.SetTag("refunddesk.decision.state", state);

        var criteria = new JsonObject();
        foreach (var (name, description) in options) criteria[name] = description;
        var body = new JsonObject
        {
            ["model"] = Deployment,
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                [question] = new JsonObject { ["type"] = "choice", ["instructions"] = instructions, ["criteria"] = criteria },
            },
        };

        var sw = Stopwatch.StartNew();
        // Buffered body: the endpoint rejects chunked requests (it requires Content-Length).
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_url, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            span.Fail($"HTTP {(int)response.StatusCode}");
            throw new HttpRequestException($"{Deployment} returned HTTP {(int)response.StatusCode}: {text}");
        }

        var json = JsonNode.Parse(text) ?? throw new JsonException("empty response");
        var answer = json["answers"]?[question] ?? throw new JsonException($"response has no answers.{question}");
        var probabilities = answer["probabilities"]?.AsObject()
            .ToDictionary(p => p.Key, p => p.Value?.GetValue<double>() ?? 0) ?? [];
        var result = new DecisionAnswer(
            answer["choice"]?.GetValue<string>() ?? throw new JsonException("answer has no choice"),
            answer["confidence"]?.GetValue<double>() ?? probabilities.GetValueOrDefault(answer["choice"]!.GetValue<string>()),
            probabilities,
            json["model"]?.GetValue<string>() ?? Deployment);

        var usage = new UsageDetails
        {
            InputTokenCount = json["usage"]?["input_tokens"]?.GetValue<long>(),
            OutputTokenCount = json["usage"]?["output_tokens"]?.GetValue<long>(),
        };
        _meter.ModelCall(Stage.Current, usage, sw.Elapsed);

        span?.SetTag("gen_ai.response.model", result.Model);
        span?.SetTag("gen_ai.usage.input_tokens", usage.InputTokenCount);
        span?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokenCount);
        span?.SetTag("refunddesk.decision.choice", result.Choice);
        span?.SetTag("refunddesk.decision.confidence", result.Confidence);
        foreach (var (name, p) in probabilities) span?.SetTag($"refunddesk.decision.p.{name}", Math.Round(p, 4));
        return result;
    }

    public void Dispose() => _http.Dispose();
}

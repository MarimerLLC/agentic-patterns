using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RefundDesk.Infrastructure;

public sealed class TelemetrySettings
{
    /// <summary>Also switched on by --otel or by setting OTEL_EXPORTER_OTLP_ENDPOINT.</summary>
    public bool Enabled { get; set; }
    /// <summary>Record prompts and responses on the gen_ai spans (sample data only in this demo).</summary>
    public bool CaptureContent { get; set; } = true;
}

/// <summary>
/// Opt-in OpenTelemetry. When it is off, nothing listens to these sources, so every
/// StartActivity call returns null and the demo runs exactly as before.
/// When it is on, traces and metrics go over OTLP (default http://localhost:4317), for
/// example to the standalone Aspire dashboard. The trace is the composed flow: one root span
/// per run, a span per stage, the parallel workers side by side, one span per tool call, and
/// the Microsoft.Extensions.AI gen_ai spans (model, tokens, prompts) underneath.
/// </summary>
public static class Telemetry
{
    public const string ServiceName = "refund-desk";
    public const string SourceName = "RefundDesk";
    public const string AiSourceName = "RefundDesk.AI";

    public static readonly ActivitySource Source = new(SourceName);

    public static bool IsOn { get; private set; }
    public static bool CaptureContent { get; private set; }
    public static string Endpoint { get; private set; } = "";

    public static IDisposable? Start(TelemetrySettings settings, bool cliSwitch)
    {
        var envEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!settings.Enabled && !cliSwitch && string.IsNullOrWhiteSpace(envEndpoint))
            return null;

        IsOn = true;
        CaptureContent = settings.CaptureContent;
        Endpoint = string.IsNullOrWhiteSpace(envEndpoint) ? "http://localhost:4317" : envEndpoint;
        var endpoint = new Uri(Endpoint);

        var resource = ResourceBuilder.CreateDefault().AddService(ServiceName, autoGenerateServiceInstanceId: false);
        var tracer = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(SourceName, AiSourceName)
            .AddOtlpExporter(o => o.Endpoint = endpoint)
            .Build();
        var meter = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(AiSourceName)
            .AddOtlpExporter(o => o.Endpoint = endpoint)
            .Build();

        return new Providers(tracer, meter);
    }

    /// <summary>Adds the standard gen_ai OpenTelemetry middleware to the chat pipeline when telemetry is on.</summary>
    public static IChatClient Instrument(IChatClient client) =>
        !IsOn ? client
            : new ChatClientBuilder(client)
                .UseOpenTelemetry(sourceName: AiSourceName, configure: c => c.EnableSensitiveData = CaptureContent)
                .Build();

    /// <summary>Display name in the trace for each pipeline stage, labelled with the talk's pattern number.</summary>
    public static string SpanName(string stage) => stage switch
    {
        StageName.Router => "② router: classify",
        StageName.OrchestratorPlan => "⑤ orchestrator: plan",
        StageName.OrchestratorSynthesize => "⑤ orchestrator: synthesize",
        StageName.OrchestratorFollowUp => "⑤ orchestrator: follow-up research",
        StageName.Evaluator => "⑥ evaluator: rubric",
        StageName.Executor => "④ tool-use loop",
        _ when StageName.WorkerSource(stage) is { } source => $"③ worker: {source}",
        _ => stage,
    };

    /// <summary>Records an "error" status on the current span (no-op when telemetry is off).</summary>
    public static void Fail(this Activity? activity, string description) =>
        activity?.SetStatus(ActivityStatusCode.Error, description);

    /// <summary>"traceId-spanId" of a span, saved in the task record so a resumed run can link back.</summary>
    public static string? ContextOf(Activity? activity) =>
        activity is null ? null : $"{activity.TraceId}-{activity.SpanId}";

    /// <summary>Links to the spans of earlier runs of the same task (stop, then --resume).</summary>
    public static IEnumerable<ActivityLink> LinksTo(IEnumerable<string> contexts)
    {
        foreach (var c in contexts)
        {
            var parts = c.Split('-');
            if (parts.Length == 2 && parts[0].Length == 32 && parts[1].Length == 16)
                yield return new ActivityLink(new ActivityContext(
                    ActivityTraceId.CreateFromString(parts[0]), ActivitySpanId.CreateFromString(parts[1]), ActivityTraceFlags.Recorded));
        }
    }

    private sealed class Providers(TracerProvider tracer, MeterProvider meter) : IDisposable
    {
        // Dispose flushes anything still batched, so the last spans reach the dashboard.
        public void Dispose()
        {
            tracer.Dispose();
            meter.Dispose();
        }
    }
}

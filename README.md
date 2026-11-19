# Agentic Design Patterns: Refund Desk demo

Companion demo for **Agentic Design Patterns: When, why, and how to use them** (VS Live Orlando 2026).

One small .NET 10 console app works a support ticket end to end and composes the patterns
from the talk, the same flow as the "Patterns compose" slide:

```
② Router → ⑦ Handoff → ⑤ Orchestrator plans → ③ parallel workers research → ⑥ Evaluator
   → ⑧ Human checkpoint → ④ Tool-use loop executes → verify by read-back
① the whole thing is a prompt chain with typed artifacts and gates
⑨ every stage is saved to a durable task record; stop anywhere and --resume
```

The customer wants a refund for a tent whose pole snapped (order #1042). They also want to
avoid being charged for a backordered stakes order (#1057).

| # | Pattern | Where | What to point at |
|---|---------|-------|------------------|
| ① | Prompt chain | `Program.cs` | Each stage hands a typed record to the next; gates between stages |
| ② | Router | `Patterns/Router.cs` | Optional decision model (Microsoft-Decision-1: calibrated probabilities, 1 output token) instead of an LLM; confidence floor → `unknown`; capabilities come from code, not the classifier; order ids come from a regex, not a model |
| ③ | Parallel fan-out | `Orchestrator.ResearchAsync` | `Task.WhenAll` over workers; each quote is checked against the source in code |
| ④ | Tool-use loop | `Patterns/Executor.cs` | Hand-written reason/act/observe loop; iteration, tool-call and time budgets; allowlist; idempotency keys on every effect, including the customer email (durable outbox) |
| ⑤ | Orchestrator–workers | `Orchestrator.PlanAsync` | The lead decides which research is needed at runtime; fan-out capped at 4 |
| ⑥ | Evaluator–optimizer | `Patterns/Evaluator.cs` | Deterministic checks first, then a model rubric; max 3 rounds; issues go back to the lead |
| ⑦ | Specialist handoff | `HandoffPackage` | Typed package, one owner, capabilities re-evaluated at the boundary |
| ⑧ | Human checkpoint | `Patterns/HumanCheckpoint.cs` | A real preview (target, account, amount, evidence, assumptions, alternatives), and the approval binds the executor's tools |
| ⑨ | Durable task + memory | `Infrastructure/TaskStore.cs` | `.data/tasks/T-*.json` is the memory, not the context window; `--resume` |

Economics (slide 15) are printed at the end: model calls, tokens, tool calls and time per stage.
They come from a `DelegatingChatClient` in the `IChatClient` pipeline.

## Run it

```bash
cd src/RefundDesk
dotnet run                       # scripted replay unless a key is configured (see below)
```

### Switches

| Switch | Effect |
|--------|--------|
| `--provider scripted\|anthropic\|openai` | Override `Llm:Provider` |
| `--model <id>` | Override `Llm:Model` |
| `--chaos` | The first payment call applies the refund **and then times out**: an ambiguous side effect |
| `--resume [task-id]` | Continue a saved task (latest if no id) |
| `--request "text"` | Use a different customer message (try something off-topic to see the router abstain) |
| `--auto-approve` | Skip the approval prompt (rehearsal only) |
| `--fast` | No simulated think time in scripted mode |
| `--reset` | Delete `.data/` (task records, the payment ledger and the message outbox) |
| `--otel` | Send OpenTelemetry traces and metrics over OTLP (see below). Off by default |

### Watching it in the Aspire dashboard (optional)

Telemetry is opt-in. It turns on with `--otel`, `Telemetry:Enabled`, or when `OTEL_EXPORTER_OTLP_ENDPOINT`
is set (default endpoint `http://localhost:4317`). When it is off, nothing is listening and the run
is unchanged. Start the standalone dashboard in its own window (no Aspire AppHost: the approval
prompt needs a real terminal):

```bash
docker run --rm -it -p 18888:18888 -p 4317:18889 -e DASHBOARD__FRONTEND__AUTHMODE=Unsecured \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
dotnet run -- --reset --chaos --otel      # then open http://localhost:18888/traces
```

The trace follows the composed flow: a root span per run, a span per stage numbered like the
slides, the four `③ worker` spans side by side, `round 1` / `round 2` of the evaluator (round 1
marked as an error with the issue it found), the `⑧ human checkpoint` span showing how long the
human took, and one span per `④` iteration and tool call, with the `TIMEOUT` marked as an error.
The `chat` spans under each stage come from `Microsoft.Extensions.AI`'s `UseOpenTelemetry()` and
carry the model, token counts and, with `Telemetry:CaptureContent` (on by default; sample data
only), the exact prompt and response. After `--resume`, the new trace links back to the earlier
runs of the same task (ids are kept in the task record). `Unsecured` is for a local demo only.

### Model configuration

The app talks to `Microsoft.Extensions.AI.IChatClient`, so the provider is configuration only.

**Claude (Anthropic SDK):**
```bash
dotnet user-secrets set "Llm:Provider" "anthropic"
dotnet user-secrets set "Llm:ApiKey" "<key>"            # or set ANTHROPIC_API_KEY
dotnet user-secrets set "Llm:Model" "claude-opus-5-5"   # default; claude-sonnet-5-5 / claude-haiku-5-5 are faster on stage
```

**Any OpenAI-compatible endpoint** (OpenAI, Azure OpenAI v1, Ollama, vLLM, OpenRouter...):
```bash
dotnet user-secrets set "Llm:Provider" "openai"
dotnet user-secrets set "Llm:Endpoint" "http://localhost:11434/v1"
dotnet user-secrets set "Llm:Model" "<model>"
dotnet user-secrets set "Llm:ApiKey" "<key or anything for local>"
```
For **Azure OpenAI / Azure AI Foundry**, the resource URL from the portal is enough
(`https://<resource>.openai.azure.com/`); the app adds `/openai/v1/`. `Llm:Model` is the
**deployment name**.

**Decision model for the router (optional).** Deploy Microsoft-Decision-1 in the same Foundry
resource and set its deployment name:
```bash
dotnet user-secrets set "Decision:Deployment" "decision-1"
```
The router then asks it a single `choice` question and gets back calibrated probabilities per
category, instead of a confidence number the LLM wrote itself. The endpoint and key default to
`Llm:Endpoint` / `Llm:ApiKey` (override with `Decision:Endpoint` / `Decision:ApiKey`). If the call
fails, the router falls back to the LLM and says so on screen. The scripted replay never uses it.

If an endpoint rejects JSON-schema response formats, set `Llm:UseJsonSchema` to `false`. The
schema then goes in the prompt instead.

**Scripted replay** (`--provider scripted`) needs no network. It is clearly labelled on screen,
and everything except the `IChatClient` is the same code. Use it as the Wi-Fi fallback. Its first
draft deliberately leaves out order #1057, so the evaluator always has something to catch.

## Demo script (about 12–15 minutes)

1. **Happy path with the chaos switch**
   `dotnet run -- --reset --chaos`
   - ② Router: point out the confidence and the floor. Capabilities are granted by code. With
     Decision-1 configured, "decided by" shows a decision model, not an LLM: real probabilities,
     1 output token, about 0.5 s (compare the router row in the economics table).
   - ⑦ Handoff: the order ids came from a regex. "Why is a model needed at this step?"
   - ⑤+③ The lead chose these 4 questions at runtime; the workers ran in parallel. Each quote was checked in code against the source.
   - ⑥ Evaluator: watch it catch a missing order. With a live model the lead often skips the
     inventory check at first; the evaluator's coverage check fails, and the lead sends a
     follow-up worker to inventory before revising (⑤ adapting at runtime). Live models
     sometimes get it right first time, which is fine.
   - ⑧ Checkpoint: compare it with "Allow agent?". Choose **Pause**.
2. **Durability.** Open `.data/tasks/T-*.json` in the editor and show that this file is the memory.
   Then run `dotnet run -- --resume --chaos` and approve.
3. **Ambiguous side effect.** The first refund returns `TIMEOUT`. The agent checks
   `get_payment_status` with the same idempotency key rather than paying again.
4. **Verify.** The ledger read-back shows exactly one effect per approved action, and the
   outbox shows the customer reply went out once, with the approved text. The send tool takes
   no text argument, so the model can trigger the email but cannot rewrite it.
5. **Economics.** The table at the end shows cost per successful task. If you ran with `--otel`,
   switch to the dashboard: the waterfall shows the parallel workers, and clicking the router's
   `chat` span shows exactly what the model saw and how many tokens it cost.
6. **Router abstains** (optional, audience participation):
   `dotnet run -- --request "What's the best trail in Colorado?"`

## Layout

```
src/RefundDesk/
  Program.cs                 the composed flow (read this top to bottom)
  Patterns/                  Router, Orchestrator, Evaluator, HumanCheckpoint, Executor (+ BillingTools)
  Domain/                    sample orders, policy knowledge base, payment gateway with idempotent ledger
  Infrastructure/            IChatClient factory + metering, durable task store, scripted replay client, console UI
```

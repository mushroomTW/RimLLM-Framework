# RimLLM Framework

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[繁體中文說明](README_zh.md)

`RimLLM Framework` is a foundational framework providing a Large Language Model (LLM) calling interface and core infrastructure for RimWorld mods. It gives other RimWorld AI mods a robust, convenient, efficient and ready-to-use SDK, so mod developers don't have to reinvent the wheel.

Everything you get back from the framework is a **standard Microsoft.Extensions.AI type** — `IChatClient`, `ChatMessage`, `ChatResponse`, `IEmbeddingGenerator`. There is no bespoke client interface to learn.

```csharp
// The whole consumer API. The player has already configured the provider,
// model, API key and fallback chain in this mod's settings.
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

> [!IMPORTANT]
> This framework is a dependency, not a standalone feature. At run time the player must have the
> **RimLLM Framework** mod active *and* have configured at least one provider with an API key —
> otherwise `RimLLMProvider.CreateChatClient` throws `InvalidOperationException` and calls fail with
> `RimLLMException`. Supported game version: **RimWorld 1.6** (see `About/About.xml`).

## Contents

* [Installation](#-installation) — reference the assemblies without shipping duplicates
* [SDK Usage](#-sdk-usage) — chat, streaming, structured output, embeddings, error handling
* [Features](#-features) — what the framework does for you
* [Architecture](#️-architecture) — how it does it, and why
* [Security notes](#-security-notes) — what each mechanism actually protects against
* [License](#-license) · [Unit tests and verification](#-unit-tests-and-verification)

---

## 📦 Installation

Your mod needs the Microsoft.Extensions.AI (MEAI) types at **compile time**, but must **not ship them at run time**. RimLLM Framework already deploys every MEAI DLL into its own `Assemblies/` folder, and RimWorld loads all mods into a single AppDomain — a second copy would create two distinct `IChatClient` types and every cast between them would fail.

The rule is the same for both options below: **reference, don't copy.**

### Option A — NuGet (recommended)

```xml
<ItemGroup>
  <!-- IChatClient / ChatMessage / ChatResponse / IEmbeddingGenerator.
       ExcludeAssets="runtime" keeps the reference but stops the DLLs
       being copied into your mod's Assemblies folder. -->
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0) — this is all a consuming mod needs. It brings in `Microsoft.Extensions.AI.Abstractions`, where `IChatClient` lives.
* [`Microsoft.Extensions.AI.OpenAI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.10.0) — additionally shipped by the framework. You only need to reference it if you construct OpenAI SDK clients yourself; a mod that just calls `RimLLMProvider.CreateChatClient` does not.

> [!IMPORTANT]
> **Pin the version to exactly `10.10.0`.** Assembly identity must match what the framework loaded. Do not set `CopyLocalLockFileAssemblies` to `true` in a consuming mod — that is what causes the duplicate-DLL problem above.
>
> **Upgrading from an earlier framework release:** MEAI's assembly version tracks `major.minor`, so `10.8.3` produced `10.8.0.0` while `10.10.0` produces `10.10.0.0`. A consuming mod compiled against an older MEAI release therefore has to bump this reference and rebuild — it is not a source-level break, but the old binary no longer matches what the framework loads.

You still need a reference to the framework assembly itself, which is not on NuGet — see Option B for that part.

### Option B — Direct DLL reference

Reference the DLLs straight out of the installed framework mod. `<Private>false</Private>` is what stops MSBuild copying them to your output.

```xml
<PropertyGroup>
  <RimLLMDir>$(MSBuildProgramFiles32)\Steam\steamapps\common\RimWorld\Mods\RimLLM Framework\Assemblies</RimLLMDir>
</PropertyGroup>

<ItemGroup>
  <Reference Include="RimLLM Framework">
    <HintPath>$(RimLLMDir)\RimLLM Framework.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="Microsoft.Extensions.AI.Abstractions">
    <HintPath>$(RimLLMDir)\Microsoft.Extensions.AI.Abstractions.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Adjust `RimLLMDir` if RimWorld is not installed under the default Steam path.

### Load order

Declare the dependency in your mod's `About/About.xml` so the framework initialises first:

```xml
<loadAfter>
  <li>GreenMushroom.RimLLMFramework</li>
</loadAfter>
```

The framework itself requires [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
(`brrainz.harmony`) for its third-party integration layer; it is declared in `About.xml` as a hard dependency.
`0Harmony.dll` is **not** shipped in `Assemblies/` — the Harmony mod provides it at run time.

---

## 💻 SDK Usage

**If you already know [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0), you already know this API.**

RimLLM Framework's entire job is to hand you a standard MEAI `IChatClient`. Everything after that one line is plain Microsoft.Extensions.AI, so this page documents only what is specific to this framework — for the MEAI calls themselves, read [Microsoft's own docs](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai).

### Only one line differs

```csharp
// The player supplies the provider, model, key and fallback chain in the mod settings
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");
```

Where another provider package would have you construct a client from a model name and an API key, you call this instead. `"myai.mod"` is just a label used for per-mod throttling and usage attribution. There is no registration call and no key handling on your side.

### Chat

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;

IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

Message lists and `ChatOptions` behave exactly as MEAI documents them. The one framework-specific rule: leave `ModelId` unset and the player's configured fallback chain decides which provider and model actually runs — set it to a `"Provider:Model"` entry to pin one.

The `ChatResponse` you get back is the provider's own, handed over unchanged apart from `ModelId`, which is rewritten to `"Provider:Model"` so you can tell who actually answered after a failover. `ResponseId`, `CreatedAt`, `ConversationId`, `Usage`, `FinishReason`, `RawRepresentation` and `AdditionalProperties` are whatever the provider set — including `null`. A `null` `Usage` means the provider reported no token counts, not that the call was free.

### Chat streaming

Streaming is MEAI's standard `GetStreamingResponseAsync` / `await foreach`. The framework adds two guarantees on top: every update is already dispatched onto the Unity main thread, so you can touch the UI directly from the loop; and if the whole fallback chain fails, the original `RimLLMException` is rethrown from `await foreach`, so a failing stream never ends silently.

### Structured output

`GetResponseObjectAsync<T>` is an extension on `IChatClient`. It generates the JSON Schema, repairs malformed output, and deserializes for you:

```csharp
public class PawnIncidentDecision
{
    public string EventType;       // "Good" or "Bad"
    public string IncidentDefName; // e.g. "RaidEnemy"
    public float Probability;
}

PawnIncidentDecision decision = await client.GetResponseObjectAsync<PawnIncidentDecision>(
    new List<ChatMessage>
    {
        new ChatMessage(ChatRole.User, "Decide the next incident type and DefName.")
    });
```

> [!NOTE]
> Use this rather than MEAI's own `GetResponseAsync<T>`. MEAI's raw schema shape (union types, `$ref`, unbounded nesting) is rejected by strict structured output on several providers, and it has no JSON-repair path. RimLLM drives the same underlying `JsonSchemaExporter` but adds a normalization layer and a single OpenAI-compatible dialect on top. See [Architecture §6](#6-official-sdks-and-provider-responsibilities).

### Native Tool Calling (Function Calling)

RimLLM Framework provides native support for Microsoft.Extensions.AI Tool Calling (`AIFunction`, `ChatOptions.Tools`, `FunctionCallContent`). Every built-in provider speaks the OpenAI protocol, so tool definitions and calls use one wire shape everywhere. `AIFunctionFactory.Create` works in-game: the framework ships the `netstandard2.0` build of `Microsoft.Extensions.AI.Abstractions`, which is what keeps MEAI's schema generation off the `System.ComponentModel.DataAnnotations` assembly RimWorld's Mono does not have (see [Architecture §6](#6-official-sdks-and-provider-responsibilities)).

#### 1. Auto-Invoking Mode with Unity Main-Thread Safety (Recommended)

When invoking tools in RimWorld, mod functions typically access game maps, pawns, or World state. To prevent Unity threading crashes, wrap your client with `AsMainThreadFunctionInvokingClient()`. This ensures that tool delegates are automatically dispatched onto the Unity main thread via `RimLLMDispatcher`:

```csharp
// Wrap the client for automatic multi-turn loop with main-thread safety
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod")
    .AsMainThreadFunctionInvokingClient(maxIterations: 10);

var weatherTool = AIFunctionFactory.Create(
    (string colonyName) => Find.CurrentMap.weatherManager.curWeather.label,
    "GetColonyWeather",
    "Returns current colony weather");

var options = new ChatOptions
{
    Tools = new List<AITool> { weatherTool }
};

// The model calls the tool, RimLLM executes it on the main thread, and returns the final answer
ChatResponse response = await client.GetResponseAsync(
    new List<ChatMessage> { new ChatMessage(ChatRole.User, "What's our colony weather right now?") },
    options);

Log.Message(response.Text);
```

#### 2. Raw / Manual Mode

If your mod wants full manual control over each turn, skip the wrapper and pass `ChatOptions.Tools` straight to `client.GetResponseAsync()`. The response then carries `FunctionCallContent` with `FinishReason = ChatFinishReason.ToolCalls`, and you drive the loop yourself exactly as MEAI documents it. Note that nothing is dispatched onto the Unity main thread on this path — that is what the wrapper above exists for.

#### 3. Notes for agent authors

Things that matter once a request turns into a multi-turn tool loop:

* **Anti-abuse throttling counts outer requests, not loop iterations.** The per-mod throttle (default 10 requests per 10 s, then a 60 s cooldown) sees every call the `FunctionInvokingChatClient` makes, but a call whose last message is a `ChatRole.Tool` result is treated as a continuation of the request that started the loop and is not counted toward the window. A mod that is already in cooldown is still blocked, continuations included. The rule keys on message shape, so it applies equally to MEAI's own `FunctionInvokingChatClient`.
* **Check up front whether tools will reach the model.** A provider without native function calling has the request's `Tools` removed before the call. `RimLLMProvider.GetEffectiveCapabilities()` returns the intersection of the capabilities of every candidate the current fallback chain could route to — if `SupportsFunctionCalling` is `true` there, no candidate can drop your tools. Pass the same `"Provider:Model"` string you would put in `ChatOptions.ModelId` to narrow it.
* **When tools were dropped anyway, the response says so.** `response.WereToolsStripped()` (and the same method on every `ChatResponseUpdate` of a stream) is `true` when the answering provider never saw the tool definitions. Treat that differently from a model that chose not to call a tool.
* **Fallback can change providers between iterations.** Each iteration is its own request, so iteration 3 may be answered by a different provider than iteration 1, carrying the earlier provider's `tool_call_id`s in the history. Every built-in provider speaks the OpenAI protocol, so this round-trips; check `response.ModelId` (`"Provider:Model"`) if your logic depends on which model is answering.
* **The framework keeps no conversation state.** History, context-window trimming and token budgeting are the caller's job; the framework only maps an overflow to `LLMError.ContextWindowExceeded`.

### Embeddings

Same shape — one framework call, then standard MEAI:

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
```

`GenerateAsync`, `GeneratedEmbeddings<T>` and `Embedding<float>` behave as MEAI documents them. Each `Embedding<float>` carries the `ModelId` that actually produced it, and `GeneratedEmbeddings.Usage` carries the input token count when the provider reports one — OpenAI-compatible endpoints do, Gemini's public API does not (its `tokenCount` is Enterprise-only), so there `Usage` stays `null`. As with chat, `null` means the provider reported nothing, not that the call was free. The embedding provider defaults to **Disabled**; until the player picks one, `GenerateAsync` throws `RimLLMException`.

### Error handling

Every failure surfaces as `RimLLMException`, with a provider-independent `LLMError` code:

```csharp
try
{
    ChatResponse response = await client.GetResponseAsync(messages);
}
catch (RimLLMException ex) when (ex.Error == LLMError.QuotaExceeded)
{
    Messages.Message("Out of API credit.", MessageTypeDefOf.RejectInput, false);
}
catch (RimLLMException ex)
{
    Log.Error($"[MyAIMod] {ex.Error}: {ex.Message}");
}
```

`LLMError` values: `Timeout`, `RateLimit`, `InvalidKey`, `ProviderOffline`, `InvalidResponse`, `NetworkError`, `ModelNotFound`, `ContentFilter`, `QuotaExceeded`, `Cancelled`, `Unknown`, `ContextWindowExceeded`.

`ContentFilter` and `ContextWindowExceeded` are split out of the generic 4xx rejection because the three cases call for different responses: shorten the prompt, try a different provider, or fix the request. Providers on the OpenAI protocol report both as plain `400`s, so the mapper identifies them from the message text; `413` maps to `ContextWindowExceeded` by definition. Schema, reasoning and temperature rejections keep reporting `InvalidResponse`, leaving the existing "retry without that parameter" path untouched. New enum members are appended at the end of `LLMError`, so already-compiled mods keep their values.

### The one optional type: `RimLLMChatOptions`

`ChatOptions` covers the standard knobs. `RimLLMChatOptions` is pure convenience: every one of its properties reads and writes `ChatOptions.AdditionalProperties` under a `rimllm_*` key, and the framework only ever reads that dictionary. Setting those keys on a plain `ChatOptions` works identically, so you never have to reference this type — and because it holds no fields of its own, a middleware that clones your options into a plain `ChatOptions` cannot silently drop your settings.

```csharp
var options = new RimLLMChatOptions
{
    Temperature = 0.7f,          // plain ChatOptions
    Priority = 5,                // higher runs earlier in the global request queue
    CachedContext = worldRules,  // large reusable prefix; enables provider-side context caching
    MinFallbackLevel = "Medium", // don't degrade below this model tier
    DisableReasoning = true      // skip thinking on reasoning-capable models
};

ChatResponse response = await client.GetResponseAsync(messages, options);
```

`EnableContextCaching` turns on automatically when `CachedContext` is non-empty. The reusable prefix is merged into the system message, so providers with server-side prompt caching (OpenAI, and Gemini through its OpenAI-compatible endpoint) discount repeated prefixes automatically.

### What you don't have to write

This is the point of the framework. All of the following already happens behind that one `IChatClient`:

| You skip | Because the framework does it |
|---|---|
| API key storage and UI | AES-256 encrypted settings with an OS-protected per-user key, shared across every mod |
| Picking a provider or model | Player-configured fallback chain, `Provider:Model` entries |
| Retry and `Retry-After` | Retries on timeout / 429 / connection error, honouring both header formats |
| Failover between providers | Automatic descent through the fallback chain before the reply starts |
| Dead-provider handling | Circuit breaker with exponential cooldown after repeated failures |
| Rate limiting across mods | Global priority queue and concurrency cap, so mods don't stutter the game |
| Cost control | Daily budget with hard-block / mock / free-tier / prompt policies |
| Usage and cost reporting | Per-provider token and cost dashboard in the Debug tab |
| Reasoning-model quirks | `reasoning_content` normalized into MEAI `TextReasoningContent` |
| Malformed JSON | Markdown fences, unclosed brackets and trailing commas repaired, with LLM-assisted double repair |
| Main-thread marshalling | Streaming chunks and log writes dispatched back to Unity's main thread |
| Native Tool Calling & Dispatching | Tools sent in the OpenAI protocol shape on every provider; automatic dispatch onto Unity main thread |

### API surface at a glance

`using RimLLM_Framework;` brings in 14 public types. Most mods only ever touch the first row:

| Tier | Types | Needed when |
|---|---|---|
| **Calling a model** | `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException`, `LLMError`, `RimLLMClientExtensions` (`GetResponseObjectAsync<T>`, `WereToolsStripped`), `RimWorldFunctionInvoker` | Always — this is the whole consumer API |
| **Supplying a provider** | `ILLMProvider`, `LLMProviderCapabilities`, `IRimLLMSettings` | Only if you register your own LLM backend via `RimLLMProvider.RegisterProvider` (`ILLMProvider` produces standard `Microsoft.Extensions.AI.IChatClient`) |
| **Diagnostics** | `TestResult`, `ProviderIds`, `LLMErrorMapper` | Connection tests, built-in provider id constants, HTTP-status mapping |

Everything else — `IChatClient`, `ChatMessage`, `ChatResponse`, `ChatResponseUpdate`, `IEmbeddingGenerator` — is `Microsoft.Extensions.AI`. The concrete client classes are `internal`, so there is deliberately no RimLLM client type to program against.

---

## 📖 Features

1. **Multi-provider support**
   * Native support for Google **Gemini**, **OpenAI**, **DeepSeek**, **Groq**, **Grok (xAI)**, **Z.ai**, **OpenRouter**, **Kimi**, **MiniMax**, **Qwen** and **NVIDIA**.
   * Supports **OpenAI-compatible APIs**, so you can configure any local or third-party compatible endpoint (LM Studio, Ollama, LocalAI, vLLM, and so on). The default endpoint is `http://localhost:1234/v1` and API keys are supported.
   * **Kimi**, **MiniMax** and **Qwen** offer a one-click "use China-specific endpoint" toggle (off by default) for better connectivity.
2. **Failover and model fallback**
   * **Client-side fallback chain**: configure a chain made up of a primary model and multiple exact fallback models. When the current model hits a timeout, rate limit (HTTP 429) or connection error, the framework switches down the chain seamlessly. The UI produces entries in `Provider:Model` form; the framework still parses bare provider entries for compatibility and uses that provider's default model.
   * **OpenRouter server-side fallback**: an OpenRouter entry may name several comma-separated models — set `ChatOptions.ModelId` to `"OpenRouter:model-a, model-b, model-c"` (a fallback-chain entry accepts the same form). The provider then sends OpenRouter's `models` array instead of a single `model` field, moving the choice among those models to OpenRouter's side; a single model name still sends a plain `model`. Pinned by `TestOpenRouterFallbackPayload`. Note the settings UI builds entries from the cached model list one model at a time, so this multi-model form comes from calling code rather than from the fallback-chain editor.
   * `Retry-After` is honoured in both forms RFC 7231 allows — delay-seconds and HTTP-date — on every path.
   * **Exponential backoff between retries**: the wait doubles per attempt (`RetryDelay × 2ⁿ`) with ±20% jitter, capped at 60 s. Retrying a rate limit at a fixed interval just hits the same wall again and burns the retry budget for nothing; a server-supplied `Retry-After` still wins when it asks for longer.
   * **Cooldowns are per provider *and* model.** The health ledger is keyed on `Provider:Model`. A chain often holds several models from one provider (three OpenRouter models, say), and keying on the provider alone let one rate-limited model take its healthy siblings down with it.
   * **One request records one failure.** Every retry of a single request counts as one failure against the health ledger. Recording each attempt meant a single network blip — 4 attempts at the default settings — pushed a healthy target straight past the circuit-breaker threshold and removed it for minutes.
   * **Routing strategies**: `PriorityFailover` (chain order), `MinLatency`, `RoundRobin`, and `LowestCost`. `LowestCost` orders candidates by the model tier the framework already derives from API pricing, so no separate price table is needed. All sorts are stable, so equally ranked candidates keep the chain's own order.
3. **AES-256 settings encryption**
   * API keys are stored with AES-256 symmetric encryption using a random key protected by the current OS user. New entries use the `v3:` format; `v1`/`v2` device-derived ciphertext is accepted only for migration and is replaced with the protected-key format on the next save.
   * Keys are also **masked in the settings UI** by default (head and tail only, so you can still tell which key is which) with a per-row toggle to reveal one for editing. This is about a different leak path from encryption: screenshots, bug reports and live streams.
   * Every provider (including Gemini) passes its API key via an HTTP header, never in the request URL, so keys don't end up in proxy or server access logs.
   * RimWorld mods all run inside the same game process. This framework makes no claim to stop a malicious mod from reading memory, reflecting over public APIs, or otherwise bypassing in-process boundaries.
4. **Polished scrollable multi-column GUI**
   * Intuitive flow-grid of model chips with click menus for one-click Fallback chain addition or name copying, and full model names in tooltips.
   * Provider menu with visual active-item accent bars and instant color-coded status badges ("Enabled", "Disabled", and "Missing Key").
   * Chat test page upgraded to modern message bubble cards with distinct User vs AI roles, Markdig AST-driven Markdown rendering, one-click reply copying at the bottom of AI bubbles, and in-flight cancellation (Stop button). Each AI bubble also carries small bottom badges showing the model that actually answered (click to copy), elapsed latency, and token usage — exact when the provider reports usage, otherwise a marked `~` estimate — and these persist with the chat history.
   * Embedding settings use the same three-column layout as the providers page: one sub-tab per embedding provider (Google Gemini, OpenAI, Ollama, OpenAI-compatible), each with its own model, endpoint and optional key, a preset list of known embedding models when nothing has been fetched yet, a local-server auto-detect button for the two local providers, and a "Test connection & embedding" button that reports vector dimensions and latency.
   * Model selection dialog equipped with a one-click clear button and quick filter chips for popular model families (Gemini, GPT, Claude, DeepSeek, etc.).
5. **Dedicated debug tab with logging control**
   * A separate **Debug** settings tab with a "Detailed Logging" checkbox, so mod developers and players can turn this mod's log output on or off while troubleshooting.
6. **One-click connection test**
   * Instant connectivity check that measures latency and validates the API key and model. Implemented once in the base class and shared by all providers.
7. **Thread safety and main-thread Scribe dispatch**
   * All settings dictionaries are guarded by locks against concurrent read/write from multiple threads.
   * Scribe writes triggered by `RecordLog` are dispatched back to the Unity main thread through `RimLLMDispatcher` with a 15-second write throttle, preventing crashes and TPS spikes caused by background saves.
8. **Reasoning models and chain-of-thought tagging**
   * Native support for modern reasoning and thinking models such as **Gemini 3.7 Flash / 3.1 Pro (Thinking)**, **OpenAI GPT-5.6 Sol / GPT-5.5**, **DeepSeek-V4-Pro / Flash**, **Grok 4.6**, **Qwen3.8-Max**, **Kimi K3** and **GLM-5.3-Flash**.
   * The framework normalizes the chain of thought returned by the API (`reasoning_content` in the OpenAI protocol) into MEAI's own `TextReasoningContent`, and hands it to you inside `ChatResponse.Messages` / `ChatResponseUpdate.Contents`. It is deliberately **not** folded into `ChatResponse.Text`: every caller that reads `Text` — structured output, the response cache key, JSON parsing — would otherwise have to strip tags out of it first. Filter on the content type to keep or drop it.
   * The GUI chat test page builds `<think>...</think>` tags from those contents itself and renders the reasoning as grey italic text. That flattening is presentation, not protocol.
   * **Reasoning effort control**: the default is "Auto", which leaves the service-side default in place (OpenAI's dynamic `reasoning_effort`, and so on). You can also disable reasoning entirely or set it manually to low / medium / high.
   * **Effort reaches every provider and every model.** Each provider declares its own wire format instead of the framework guessing from model names: top-level `reasoning_effort` (OpenAI, Gemini via its OpenAI-compatible endpoint, xAI, Groq, MiniMax, NVIDIA, OpenAI-compatible endpoints), OpenRouter's unified `reasoning` object, `thinking: {type}` plus effort (DeepSeek, Z.ai, Kimi) and `enable_thinking` with `thinking_budget` (Qwen). Vocabulary differences are mapped per provider — Kimi only accepts low/high/max, and xAI cannot disable reasoning at all, so a disable request is ignored there rather than turned into a 400.
   * **Unknown models are handled optimistically, then learned.** Model-name allow-lists rot: the framework previously sent effort only for names starting with `o1`/`o3`, silently dropping the setting everywhere else. Now the effort is sent unless the model is on a short deny-list of known non-reasoning families. If the service rejects the parameter with a 400, the framework records that `(provider, model)` pair, retries the request once without it, and stops sending it for the rest of the session. Missing a model therefore costs one retry instead of failing permanently. The same mechanism covers `temperature`, which reasoning models such as the GPT-5 series reject outright. The memory is per game session, so a model that gains support later is retried after a restart.
   * **Markdown rendering**: the chat test page uses the **Markdig AST parser** to convert model replies into Unity legacy rich text, so headings, bold, italics, lists, block quotes, links and code blocks render as structure instead of raw `**` and `` ` `` characters. The legacy IMGUI text system only understands `b`, `i`, `size`, `color`, `material` and `quad`, so structure with no matching tag (indentation, tables) is approximated with spacing and symbols. Underscore italics are deliberately unsupported because they collide with `snake_case` identifiers.
9. **Context caching and prompt caching**
   * Set `CachedContext` in `RimLLMChatOptions` and the framework merges it into the system message, so providers with server-side prompt caching (OpenAI, and Gemini through its OpenAI-compatible endpoint) discount repeated prefixes — significantly reducing input token cost and latency for high-frequency repeated requests.
   * **Quantified savings**: usage tracking parses the cache-hit tokens returned by the API (OpenAI `cached_tokens` and equivalents) and applies a discounted rate to the cost estimate, so the cost panel reflects the real saving.
   * **Local response cache** (off by default, and a different thing from the two bullets above — those are the *provider's* cache, this one never leaves the player's machine). When enabled, a byte-identical request replays the previous answer with no API call at all: zero cost, zero latency, and no token usage recorded. The key covers everything that changes the output — every message (role, text, and non-text content such as tool results), the target model, minimum fallback level, cached context, temperature, max output tokens, reasoning effort, whether reasoning is disabled, the structured-output type, and every sampling parameter that reaches the provider verbatim (`TopP`, `TopK`, `FrequencyPenalty`, `PresencePenalty`, `Seed`, `StopSequences`) — but deliberately not `modId` or `Priority`, which only affect throttling and queue order. Matching is exact, not semantic. The trade-off is that identical input always produces identical output, which is not what narrative text usually wants; that is why it ships off, with a player-set TTL (1–120 minutes, fixed at the moment an entry is written) and a 256-entry cap. Expiry and capacity eviction are managed by an internal lightweight store (256-entry cap, FIFO with TTL eviction) to eliminate external caching dependencies and RimWorld AppDomain assembly version drift, leaving the framework to decide only what counts as the same request. Memory only — nothing is written to the save file.
10. **Embedding SDK**
    * The framework exposes public embedding functionality backed by Google Gemini, OpenAI, Ollama or an OpenAI-compatible endpoint. Other mods obtain a standard `IEmbeddingGenerator` through `RimLLMProvider.CreateEmbeddingGenerator` for semantic search and clustering.
    * All online sources go through the OpenAI SDK: Google reaches Gemini through its official OpenAI-compatible endpoint, OpenAI uses its native endpoint; Ollama and self-hosted services use the OpenAI SDK's `EmbeddingClient` (Ollama via its OpenAI-compatible `/v1` endpoint). The *Embedding endpoint* field therefore takes a **service root address** such as `http://localhost:11434/v1`; a full `/embeddings` path is normalized automatically. Model, endpoint and key are stored per embedding provider, so switching the active provider does not lose the others' settings; a blank model or endpoint means "use the provider default", and a blank key inherits the matching chat provider's key.
    * The settings page can fetch the available model list instead of requiring the name to be typed from memory. OpenAI-compatible `/v1/models` reports no capability information, so that list is **ordered** (embedding-looking names first) rather than filtered — a server's model names may be user-defined, and filtering would hide valid choices. Manual entry always remains available for servers with no `/v1/models` endpoint.
    * Embeddings are a billed API, so they share the same anti-abuse checks as ordinary generation requests. Their keys use the same AES encryption as provider keys.
11. **Native Tool Calling (Function Calling)**
    * Full support for Microsoft.Extensions.AI Tool Calling (`AIFunction`, `ChatOptions.Tools`, `FunctionCallContent`, `FunctionResultContent`).
    * Full support for Microsoft.Extensions.AI Tool Calling (`AIFunction`, `ChatOptions.Tools`, `FunctionCallContent`, `FunctionResultContent`) on every provider through the shared OpenAI-protocol path.
    * Main-thread safety scheduling via `RimWorldFunctionInvoker.AsMainThreadFunctionInvokingClient()`, automatically dispatching tool execution onto the Unity main thread to prevent RimWorld threading crashes.
    * Automatically bypasses local response cache when tools are present to ensure execution consistency.
12. **Third-party integration (force other mods through RimLLM)**
    * The *Integrations* settings tab lists mods whose LLM traffic RimLLM can take over — currently **RimTalk** (`cj.rimtalk`), **Auto Translation** (`seohyeon.autotranslation`), and **Mod Compatibility Checker** (`modcompatchecker.main`). With the toggle on, all requests from the target mod are redirected into RimLLM's fallback chain, budget, throttling, response cache, and usage statistics. The target mod's own API key / model / endpoint settings are ignored while on; toggles take effect immediately without restarting and default to **off**.
    * **RimTalk**: Prompt engineering is untouched; messages (including the "Output JSONL" instruction) are forwarded verbatim, and streamed text is fed chunk-by-chunk into RimTalk's own `JsonStreamParser`, so speech bubbles still appear line by line. Reasoning content is kept out of JSONL, and thinking is disabled for dialogue speed.
    * **Auto Translation**: Fully supports single-item and XML batch translations. Uses the batch-capable and placeholder-safe `Translator_OpenAICompatible` as a sentinel adapter; placeholder protection (`__PH0__`, etc.) and batch XML handling are fully retained. Current translator automatically synchronizes with the toggle, falling back to native engine with throttled warnings if offline.
    * **Mod Compatibility Checker**: Intercepts `AIService.CallAPIWithTimeout` to route Harmony/XML conflict analysis and diagnostic prompts through RimLLM. Automatically overrides `IsAIConfigured` to `true` when takeover is active, allowing players to run AI analysis without entering duplicate API keys in the checker, while short-circuiting balance checks to avoid 401 errors.
    * Pure Harmony, fail-soft everywhere; the framework implements none of the third-party interfaces and never references third-party types in base classes, interfaces, or fields, allowing the framework to safely load when the target mod is absent. If an API has drifted the hook fails to attach and the settings tab shows the reason.
    * Adding another mod is a registry entry plus one `RimLLMCompatTarget` subclass; adapters compile against checked-in reference DLLs (`Source/Libs/`) that are not shipped.

---

## 🛠️ Architecture

### 1. Unified interface and dispatch core (`IChatClient` / `IEmbeddingGenerator` and `RimLLMProvider`)

* The framework exposes the standard Microsoft.Extensions.AI interfaces. Callers only work against `IChatClient` or `IEmbeddingGenerator` and never need to know which provider or model handled the request — `RimLLMManager` handles dispatch and fallback rotation.
* `CreateChatClient` returns a stack of MEAI `DelegatingChatClient` middleware — reasoning-effort normalization, response cache, anti-abuse throttle, budget guard, priority queue — terminating in a `FailoverChatClient` that routes across the fallback chain. Every layer is `internal`. The only framework-specific types a consumer touches are `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException` and `LLMError`; everything else crossing the boundary is a MEAI type.
* `modId` is a plain label, not a credential. It keys per-mod anti-abuse throttling and telemetry attribution, and requires no registration call.

### 2. Unity main-thread dispatcher (`RimLLMDispatcher`)

* Network requests run asynchronously on background thread-pool threads, but most Unity APIs and RimWorld logic are not thread safe — calling them from a background thread causes crashes or TPS spikes.
* `RimLLMDispatcher` is a MonoBehaviour singleton that collects callbacks from background threads in a `ConcurrentQueue` and dispatches them back to the main thread during Unity's per-frame `Update`.

### 3. Streaming bridge (`Channel<T>`)

* The executor's streaming API is callback-shaped (`Action<ChatResponseUpdate> onUpdateReceived`), while MEAI expects `IAsyncEnumerable<ChatResponseUpdate>`. The bridge between them is an unbounded `System.Threading.Channels.Channel<T>`; the consumer side is simply `ChannelReader.ReadAllAsync()`.
* Updates cross that bridge **verbatim** — the object the provider produced is the object you enumerate, with only `ModelId` rewritten. The framework no longer synthesizes a closing update of its own, so `UsageContent`, `FinishReason` and `ResponseId` are present exactly when the provider emits them.
* Because `IAsyncEnumerable` reaches this project through the `bclasync` extern alias, C# 8 cannot compile an async iterator over it. `ReadAllAsync()` sidesteps that entirely: it returns the same assembly's `IAsyncEnumerable`, so no iterator has to be hand-written.
* A thin wrapper unwraps `ChannelClosedException` so producer failures surface to callers as the original `RimLLMException`.

### 4. Unified HTTP error mapping (`LLMErrorMapper`)

* The rules that translate HTTP status codes into `LLMError` live in a single place, `LLMErrorMapper`, shared by the official SDK path (`ClientResultException`) and the embedding service.
* `Retry-After` parsing lives there too, built on `RetryConditionHeaderValue`, so both the delay-seconds and HTTP-date forms are handled identically everywhere.
* As a result, "which status codes are retryable" and "which indicate a rejected schema that should be downgraded" behave identically everywhere. Third-party custom providers can reference the same mapper.

### 5. Fault-tolerant structured output (structured output & JSON repair)

* Developers frequently need the model to return a specific JSON shape.
* The built-in OpenAI-family providers prefer the official SDK's native structured output through `IChatClient`'s JSON Schema response format (Gemini included, via its OpenAI-compatible endpoint). The framework validates required members and null state before deserializing into the target C# object.
* The schema itself is generated in a single OpenAI-compatible dialect — all members land in `required`, and optionality is expressed as a `["integer","null"]` union. See [Architecture §6](#6-official-sdks-and-provider-responsibilities).
* The `RepairJson` fallback is only used when the provider has no native schema support, the service rejects the schema, or the model still returns malformed output. It handles Markdown fences (such as ` ```json `), unclosed brackets, trailing commas and JSON block extraction.

### 6. Official SDKs and provider responsibilities

* The main project and the test project stay on `net472`; RimWorld mods are not required to move to .NET 8. The official SDKs' dependency DLLs ship with the mod, and `ProviderSdkIntegrationTests` loads each one and reflects over it so a missing transitive assembly fails the build rather than the game. That check runs on real .NET Framework, so it cannot catch a type that exists there but is absent from RimWorld's Mono BCL — the `DataAnnotations` case below is exactly that failure mode, which is why it is pinned by inspecting the shipped assembly's reference list rather than by executing code. Although .NET Framework treats `System.ValueTuple` as a framework assembly, the build explicitly deploys its `4.0.5.0` DLL to avoid a `ReflectionTypeLoadException` when RimWorld's Mono reflects over MEAI.
* **OpenAI** uses the `OpenAI` SDK `2.13.0` together with `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` `10.10.0`. The framework works around an OpenAI SDK 2.13.0 / `System.ClientModel` 1.15.0 bug (where the experimental `ChatCompletionOptions.Patch` API threw a `NullReferenceException` inside `PropagateSet`) via `OpenAIPatchExtensions.DisablePatchPropagators()`, safely enabling direct JSON Patch manipulation for `reasoning_effort`, `response_format`, `max_tokens`, and `models`. The built-in `OpenAIProvider` enters the shared manager through `ChatClient.AsIChatClient()`. Only endpoints that genuinely implement the OpenAI Chat Completions protocol (LM Studio, Ollama, vLLM, …) are suitable for the OpenAI-compatible adapter.
* **Gemini** is served through Google's official OpenAI-compatible endpoint (`https://generativelanguage.googleapis.com/v1beta/openai/`), so text, streaming, native schema, thinking and tool calling all reuse the shared `OpenAIProvider` implementation. Gemini is a thin `OpenAIProvider` subclass declaring only its endpoint and default test model — the same shape as the Groq or Qwen providers.
* **Every built-in provider goes through the OpenAI SDK**: the whole family (OpenAI, Gemini, OpenRouter, DeepSeek, Groq, Grok, Z.ai, Kimi, MiniMax, Qwen, NVIDIA, OpenAICompatible) uses the `OpenAI` SDK `2.13.0` plus MEAI's `IChatClient`. Model listings use `OpenAIModelClient.GetModelsAsync()` rather than hand-rewriting the `/models` URL and parsing JSON.
* **There is no raw HTTP path and no second SDK left anywhere.** Removing the last native `Google.GenAI` path deleted the framework's entire second transport, its auth handling, and the Google `cachedContents` explicit-cache machinery along with it: `CachedContext` is now always delivered inline in the system message, and caching is whatever the provider applies server-side to repeated prefixes.
* **JSON Schema generation is `System.Text.Json`'s `JsonSchemaExporter` plus a normalization layer** (`RimLLMSchemaBuilder`), in three stages. **Stage A** exports full JSON Schema through the exporter. **Stage B** normalizes it into a restricted subset every provider accepts: `$ref` pointers are resolved and inlined, cycles and over-deep nesting are truncated, nullable unions collapse to a single `type`, and only a keyword whitelist survives. **Stage C** applies the single OpenAI-compatible dialect, expressing optional members as `["integer","null"]` unions.
  * **`Microsoft.Extensions.AI.Abstractions` ships as its `netstandard2.0` build, not the `net462` build NuGet would pick for a `net472` project.** The `net462` build reads `[EmailAddress]`, `[Range]` and friends to enrich schemas, and that code references the framework assembly `System.ComponentModel.DataAnnotations`. RimWorld's Unity Mono does not ship that DLL, so in-game `AIFunctionFactory.Create` and `AIJsonUtilities.CreateJsonSchema` threw `TypeLoadException: Could not resolve type … 'EmailAddressAttribute' in assembly 'System.ComponentModel.DataAnnotations, Version=4.0.0.0'` — even for a parameterless tool, and invisible to unit tests, which run on a real .NET Framework that has it in the GAC. The `netstandard2.0` build excludes that whole block under `#if NET || NETFRAMEWORK`, depends only on `System.Text.Json`, and carries the same `10.10.0.0` assembly version, so `Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.OpenAI` bind to it unchanged. The csproj references that build explicitly; `ShippedAbstractionsHasNoDataAnnotationsDependency` inspects the shipped DLL's reference list and fails if the selection ever reverts. Consuming mods are unaffected: they compile against the package and run against whatever the framework ships.
  * **The exporter is still called directly rather than through MEAI's `AIJsonUtilities.CreateJsonSchema` wrapper.** It is the same engine MEAI uses internally; calling it directly hands Stage B raw exporter output with no wrapper rewrites in between, so normalization only has to handle one shape. The one thing MEAI adds that is still wanted, `[Description]`, is read by Stage B itself.
  * Two consequences of using the exporter directly: it emits `{"enum":[…]}` with no `type` keyword for enums (Stage B infers the type from the enum values, otherwise every enum member would vanish), and it has no `description` concept at all (Stage B reads `[Description]` on both members and types).
  * **Cycles are cut at the CLR type level, not at the JSON pointer level.** The exporter expands a recursive member one full round before emitting the `$ref` back to the ancestor, so pointer-based detection ships an extra layer — measured at 789 → 3119 characters for the recursive test type, paid in prompt tokens on every request. `RecursiveSchemaStaysCompact` guards this.
  * **The nesting limit follows the strict structured-output cap.** OpenAI's strict structured output allows at most 5 levels of nesting (and 100 object properties in total); exceeding it gets the schema rejected and silently downgraded to prompt-based JSON, so the builder truncates at 5. Note the 100-property cap is **not** enforced yet.
  * Raw exporter output cannot be sent as-is: nullable members come out as `["string","null"]` unions that must be normalized before sending, and `$ref` pointers must be resolved — both covered by unit tests rather than left as claims in this document.
  * `$ref` is **not** only used for recursion — MEAI also emits it to deduplicate a repeated type, so blanket-truncating every `$ref` would silently delete ordinary members. The normalizer resolves the JSON pointer and only treats it as a cycle when it points at an ancestor on the current expansion path.
  * **Every member is listed in `required`**; optionality is carried by the type instead. OpenAI's strict structured output requires `required` to cover all properties, so the previous behaviour (leaving `Nullable<T>` out of `required` while still sending `strict: true`) was rejected server-side and silently downgraded to prompt-based JSON.
  * Schema generation and deserialization both run on System.Text.Json under a single shared contract (fields included, `[JsonIgnore]`/`[JsonPropertyName]` honoured, read-only members dropped), and a test asserts the member sets match. **Do not use a custom `JsonConverter` on structured-output types** — it changes the wire shape in a way the exporter cannot see. Child-mod migration note: Newtonsoft's `[JsonProperty("x")]` must become STJ's `[JsonPropertyName("x")]`, and `Newtonsoft.Json.JsonIgnoreAttribute` must become `System.Text.Json.Serialization.JsonIgnoreAttribute` — old attributes are silently ignored after the migration.
  * If `JsonSchemaExporter` is ever unavailable in RimWorld's Mono runtime, the builder logs a warning, permanently falls back to the previous reflection implementation, and forces `strict` off.
* Provider-specific SDKs never appear in `RimLLMManager` or the public SDK façade; the shared layer depends only on `IChatClient`, `LLMProviderCapabilities` and the existing `ILLMProvider` API. API keys always come from the encrypted settings and are never written into source code or ordinary logs.

---

## 🔐 Security notes

To avoid misunderstanding, here is an honest description of what each security mechanism actually protects against:

* **API key encryption uses an OS-protected per-user key.** New entries use AES-256 with a random storage key wrapped by the current user's OS protection. Legacy `v1`/`v2` entries still use the old device-derived material only so they can be migrated; new data never derives its storage key from a source-embedded seed or `deviceUniqueIdentifier`. This protects copied settings from decryption by another user or machine, but it **cannot** defend against code running in the same user context or the same RimWorld process (including other mods), where the framework must use the key to serve requests.
* **`modId` is an attribution label, not authentication.** Per-mod throttling preserves fair use for cooperative mods, while a shared safety ceiling (ten times the configured per-mod window) counts every actual provider call, including tool-loop continuations, and prevents rotating labels from creating unlimited provider calls. Same-process mods are still not isolated, so do not treat the SDK as a hostile-mod sandbox.
* **Keys never reach URLs or logs.** All providers pass keys via HTTP headers; log output always goes through `SanitizeForLog` and is length-truncated, and the device identifier is masked in diagnostic exports.
* **Provider output is untrusted UI input.** ChatTest keeps only its own grey-thinking color wrapper as rich text; raw provider HTML tags are displayed literally before Markdown reaches Unity IMGUI.

---

## 📜 License

This mod's source code is released under the **MIT License** — Copyright (c) 2026 **mushroomTW**. See [LICENSE](LICENSE).

Redistributed dependency assemblies in `Assemblies/` keep their own licenses: Microsoft.Extensions.AI and the OpenAI .NET SDK are MIT. RimWorld's own assemblies belong to Ludeon Studios and are not redistributed here.

---

## 🧪 Unit tests and verification

The project ships with a full unit test suite in `Source/RimLLM Framework.Tests` (a standalone project alongside the main one) covering AES encryption/decryption, model fallback, JSON Schema generation (normalization and repair), HTTP error mapping, `Retry-After` parsing, `ChatOptions` cloning, streaming retries and budget control.

> **Prerequisite**: the tests need RimWorld's `Assembly-CSharp` and Unity DLLs at run time. Those files are not redistributable, so a local RimWorld installation is required.
> The default path is `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed`,
> and can be overridden with the MSBuild property `RimWorldManagedDir` or the environment variable `RIMWORLD_MANAGED_DIR`.

Build and test from the project root with the `dotnet` CLI:

```bash
# Restore and rebuild the solution
dotnet build "Source/RimLLM Framework.slnx"

# Run all NUnit unit tests
dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"
```

> **Note**: the `Krafs.Rimworld.Ref` reference assemblies do not restrict the BCL surface, so it is possible to
> write code that compiles but fails inside RimWorld's Mono runtime. Known examples: `Stack<T>` throws
> `TypeLoadException`, and the parameterless `String.TrimEnd()` overload does not exist.
> Always verify with an actual `dotnet test` run rather than relying on a successful build.

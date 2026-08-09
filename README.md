# RimLLM Framework

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[繁體中文說明](README_zh.md)

`RimLLM Framework` is a foundational framework providing a Large Language Model (LLM) calling interface and core infrastructure for RimWorld mods. It gives other RimWorld AI mods a robust, convenient, efficient and ready-to-use SDK, so mod developers don't have to reinvent the wheel.

Everything you get back from the framework is a **standard Microsoft.Extensions.AI type** — `IChatClient`, `ChatMessage`, `ChatResponse`, `IEmbeddingGenerator`. There is no bespoke client interface to learn.

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
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.8.3" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.8.3) — this is all a consuming mod needs. It brings in `Microsoft.Extensions.AI.Abstractions`, where `IChatClient` lives.
* [`Microsoft.Extensions.AI.OpenAI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.8.3) — additionally shipped by the framework. You only need to reference it if you construct OpenAI SDK clients yourself; a mod that just calls `RimLLMProvider.CreateChatClient` does not.

> [!IMPORTANT]
> **Pin the version to exactly `10.8.3`.** Assembly identity must match what the framework loaded. Do not set `CopyLocalLockFileAssemblies` to `true` in a consuming mod — that is what causes the duplicate-DLL problem above.

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

---

## 💻 SDK Usage

**If you already know [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.8.3), you already know this API.**

RimLLM Framework's entire job is to hand you a standard MEAI `IChatClient`. Every line after that is plain Microsoft.Extensions.AI — exactly the same calls you would write against [`Microsoft.Extensions.AI.OpenAI`](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.8.3), Ollama, or any other provider package.

### Only one line differs

```csharp
// Microsoft.Extensions.AI.OpenAI — you supply the model and the API key
IChatClient client =
    new OpenAI.Chat.ChatClient("gpt-4o-mini", Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
    .AsIChatClient();

// RimLLM Framework — the player supplies the provider, model, key and fallback chain in the mod settings
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");
```

`"myai.mod"` is just a label used for per-mod throttling and usage attribution. There is no registration call and no key handling on your side.

### Chat

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;

IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

With a message list and options — still plain MEAI:

```csharp
var messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a cold, random and unpredictable storyteller."),
    new ChatMessage(ChatRole.User, "Greet the player in the voice of Randy Random.")
};

ChatResponse response = await client.GetResponseAsync(
    messages,
    new ChatOptions { Temperature = 0.7f, MaxOutputTokens = 150 });
```

Leave `ModelId` unset and the player's configured fallback chain decides which provider and model actually runs.

### Chat streaming

```csharp
await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("Write a colony radio broadcast."))
{
    // Already dispatched onto the Unity main thread — safe to touch the UI
    MyGameUI.AppendText(update.Text);
}
```

If the whole fallback chain fails, the original `RimLLMException` is rethrown from `await foreach`, so a failing stream never ends silently.

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
> Use this rather than MEAI's own `GetResponseAsync<T>`. MEAI's `AIJsonUtilities.CreateJsonSchema` cannot even run inside RimWorld's Mono runtime (it pulls in `System.ComponentModel.DataAnnotations`, which is absent there), its raw schema shape (union types, `$ref`) is not accepted by Google Gemini, and it has no JSON-repair path. RimLLM drives the same underlying `JsonSchemaExporter` but adds a normalization layer and per-provider dialects on top. See [Architecture §6](#6-official-sdks-and-provider-responsibilities).

### Embeddings

Also a standard MEAI interface:

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");

GeneratedEmbeddings<Embedding<float>> result =
    await generator.GenerateAsync(new[] { "colonist mental break" });

ReadOnlyMemory<float> vector = result[0].Vector;
```

The embedding provider defaults to **Disabled**; until the player picks one, `GenerateAsync` throws. For a fallback that never needs an API, use the standalone trigram helper — a plain string-similarity function, unaffected by the provider setting:

```csharp
float similarity = RimLLMEmbeddingService.CalculateTrigramSimilarity(
    "colonist mental break", "pawn had a breakdown");
```

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

`LLMError` values: `Timeout`, `RateLimit`, `InvalidKey`, `ProviderOffline`, `InvalidResponse`, `NetworkError`, `ModelNotFound`, `ContentFilter`, `QuotaExceeded`, `Cancelled`, `Unknown`.

### The one extra type: `RimLLMChatOptions`

`ChatOptions` covers the standard knobs. Subclass `RimLLMChatOptions` only when you want something MEAI has no concept of — everything else keeps working exactly as before:

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

`EnableContextCaching` turns on automatically when `CachedContext` is non-empty. Gemini caches `SystemPrompt + CachedContext` (TTL 300s) and falls back to a normal `systemInstruction` when the content is too small to be worth the cache creation fee; OpenAI applies prompt caching to repeated prefixes server-side.

### What you don't have to write

This is the point of the framework. All of the following already happens behind that one `IChatClient`:

| You skip | Because the framework does it |
|---|---|
| API key storage and UI | AES-256 encrypted settings, shared across every mod |
| Picking a provider or model | Player-configured fallback chain, `Provider:Model` entries |
| Retry and `Retry-After` | Retries on timeout / 429 / connection error, honouring both header formats |
| Failover between providers | Automatic descent through the fallback chain, mid-stream if needed |
| Dead-provider handling | Circuit breaker with exponential cooldown after repeated failures |
| Rate limiting across mods | Global priority queue and concurrency cap, so mods don't stutter the game |
| Cost control | Daily budget with hard-block / mock / free-tier / prompt policies |
| Usage and cost reporting | Per-provider token and cost dashboard in the Debug tab |
| Reasoning-model quirks | `reasoning_content` and Gemini `thought` normalized into `<think>...</think>` |
| Malformed JSON | Markdown fences, unclosed brackets and trailing commas repaired, with LLM-assisted double repair |
| Main-thread marshalling | Streaming chunks and log writes dispatched back to Unity's main thread |

### API surface at a glance

`using RimLLM_Framework;` brings in 13 public types. Most mods only ever touch the first row:

| Tier | Types | Needed when |
|---|---|---|
| **Calling a model** | `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException`, `LLMError`, `RimLLMClientExtensions` | Always — this is the whole consumer API |
| **Supplying a provider** | `IChatClientProvider`, `IChatOptionsCustomizer`, `INativeStructuredOutputProvider`, `LLMProviderCapabilities`, `IRimLLMSettings` | Only if you register your own LLM backend via `RimLLMProvider.RegisterProvider` (`ILLMProvider` lives in `RimLLM_Framework.Providers`) |
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
   * **OpenRouter server-side auto fallback (`openrouter/auto`)**: you can put OpenRouter's official `openrouter/auto` model in the fallback chain and let OpenRouter pick among its recommended models server-side.
   * `Retry-After` is honoured in both forms RFC 7231 allows — delay-seconds and HTTP-date — on every path.
3. **AES-256 settings encryption**
   * API keys are stored with AES-256 symmetric encryption, reducing the risk of plaintext keys sitting in the settings file. This is obfuscation-grade protection — see [Security notes](#-security-notes) below.
   * Every provider (including Gemini) passes its API key via an HTTP header, never in the request URL, so keys don't end up in proxy or server access logs.
   * RimWorld mods all run inside the same game process. This framework makes no claim to stop a malicious mod from reading memory, reflecting over public APIs, or otherwise bypassing in-process boundaries.
4. **Polished scrollable multi-column GUI**
   * An intuitive flow-grid of model chips with highlighted selection and full model names in tooltips.
5. **Dedicated debug tab with logging control**
   * A separate **Debug** settings tab with a "Detailed Logging" checkbox, so mod developers and players can turn this mod's log output on or off while troubleshooting.
6. **One-click connection test**
   * Instant connectivity check that measures latency and validates the API key and model. Implemented once in the base class and shared by all providers.
7. **Thread safety and main-thread Scribe dispatch**
   * All settings dictionaries are guarded by locks against concurrent read/write from multiple threads.
   * Scribe writes triggered by `RecordLog` are dispatched back to the Unity main thread through `RimLLMDispatcher` with a 15-second write throttle, preventing crashes and TPS spikes caused by background saves.
8. **Reasoning models and chain-of-thought tagging**
   * Native support for reasoning models such as **DeepSeek-R1**, **Gemini 2.0/2.5 Thinking** and **OpenAI o1/o3**.
   * The framework extracts the chain of thought returned by the API (`reasoning_content` in the OpenAI protocol, the `thought` field in Gemini) and wraps it uniformly in `<think>...</think>` tags.
   * The GUI chat test page parses these tags and renders the reasoning as grey italic text. Calling mods can easily strip or keep the chain of thought with a regular expression.
   * **Reasoning effort control**: the default is "Auto", which lets each provider run its own adaptive or dynamic thinking configuration (Gemini's `thinkingBudget = -1`, OpenAI's dynamic `reasoning_effort`, and so on). You can also disable reasoning entirely or set it manually to low / medium / high.
   * **Effort reaches every provider and every model.** Each provider declares its own wire format instead of the framework guessing from model names: top-level `reasoning_effort` (OpenAI, xAI, Groq, MiniMax, NVIDIA, OpenAI-compatible endpoints), OpenRouter's unified `reasoning` object, `thinking: {type}` plus effort (DeepSeek, Z.ai, Kimi), `enable_thinking` with `thinking_budget` (Qwen), and `thinkingConfig` for Gemini. Vocabulary differences are mapped per provider — Kimi only accepts low/high/max, and xAI cannot disable reasoning at all, so a disable request is ignored there rather than turned into a 400.
   * **Unknown models are handled optimistically, then learned.** Model-name allow-lists rot: the framework previously sent effort only for names starting with `o1`/`o3`, silently dropping the setting everywhere else. Now the effort is sent unless the model is on a short deny-list of known non-reasoning families. If the service rejects the parameter with a 400, the framework records that `(provider, model)` pair, retries the request once without it, and stops sending it for the rest of the session. Missing a model therefore costs one retry instead of failing permanently. The same mechanism covers `temperature`, which reasoning models such as the GPT-5 series reject outright. The memory is per game session, so a model that gains support later is retried after a restart.
   * **Markdown rendering**: the chat test page converts model replies to Unity legacy rich text, so headings, bold, italics, lists, block quotes, links and code blocks render as structure instead of raw `**` and `` ` `` characters. The legacy IMGUI text system only understands `b`, `i`, `size`, `color`, `material` and `quad`, so structure with no matching tag (indentation, tables) is approximated with spacing and symbols. Underscore italics are deliberately unsupported because they collide with `snake_case` identifiers.
9. **Context caching and prompt caching**
   * Native support for **Gemini context caching** and **OpenAI prompt caching**. Set `CachedContext` in `RimLLMChatOptions` and the framework submits `SystemPrompt + CachedContext` for caching, significantly reducing input token cost and latency for high-frequency repeated requests.
   * **Cost guard**: Gemini explicit caching has a minimum size threshold. When the content is too small the framework skips the cache and uses `systemInstruction` instead, avoiding the case where the creation fee is never recouped. Cache creation for the same context is also serialized by a lock to prevent duplicate resources.
   * **Quantified savings**: usage tracking parses the cache-hit tokens returned by the API (OpenAI `cached_tokens`, Gemini `cachedContentTokenCount`) and applies a discounted rate to the cost estimate, so the cost panel reflects the real saving.
10. **Embedding SDK**
    * The framework exposes public embedding functionality backed by Google, Ollama or an OpenAI-compatible endpoint, plus a cosine similarity helper. Other mods obtain a standard `IEmbeddingGenerator` through `RimLLMProvider.CreateEmbeddingGenerator` for semantic search, clustering or similarity comparison.
    * All three online sources go through official SDKs: Google uses `EmbedContentAsync` from `Google.GenAI`; Ollama and self-hosted services use the OpenAI SDK's `EmbeddingClient` (Ollama via its OpenAI-compatible `/v1` endpoint). The *Embedding endpoint* field therefore takes a **service root address** such as `http://localhost:11434/v1`; a full `/embeddings` path is normalized automatically.
    * `CalculateTrigramSimilarity` is a **separate**, API-free string-similarity utility — not an embedding provider. It produces no vectors and is callable regardless of which provider (or none) is selected.
    * Embeddings are a billed API, so they share the same anti-abuse checks as ordinary generation requests. Their keys use the same AES encryption as provider keys.

---

## 🛠️ Architecture

### 1. Unified interface and dispatch core (`IChatClient` / `IEmbeddingGenerator` and `RimLLMProvider`)

* The framework exposes the standard Microsoft.Extensions.AI interfaces. Callers only work against `IChatClient` or `IEmbeddingGenerator` and never need to know which provider or model handled the request — `RimLLMManager` handles dispatch and fallback rotation.
* The concrete facades (`RimLLMChatClient`, `RimLLMEmbeddingClient`) are `internal`. The only framework-specific types a consumer touches are `RimLLMProvider`, `RimLLMChatOptions`, `RimLLMException` and `LLMError`; everything else crossing the boundary is a MEAI type.
* `modId` is a plain label, not a credential. It keys per-mod anti-abuse throttling and telemetry attribution, and requires no registration call.

### 2. Unity main-thread dispatcher (`RimLLMDispatcher`)

* Network requests run asynchronously on background thread-pool threads, but most Unity APIs and RimWorld logic are not thread safe — calling them from a background thread causes crashes or TPS spikes.
* `RimLLMDispatcher` is a MonoBehaviour singleton that collects callbacks from background threads in a `ConcurrentQueue` and dispatches them back to the main thread during Unity's per-frame `Update`.

### 3. Streaming bridge (`Channel<T>`)

* The manager's streaming API is callback-shaped (`Action<string> onChunkReceived`), while MEAI expects `IAsyncEnumerable<ChatResponseUpdate>`. The bridge between them is an unbounded `System.Threading.Channels.Channel<T>`; the consumer side is simply `ChannelReader.ReadAllAsync()`.
* Because `IAsyncEnumerable` reaches this project through the `bclasync` extern alias, C# 8 cannot compile an async iterator over it. `ReadAllAsync()` sidesteps that entirely: it returns the same assembly's `IAsyncEnumerable`, so no iterator has to be hand-written.
* A thin wrapper unwraps `ChannelClosedException` so producer failures surface to callers as the original `RimLLMException`.

### 4. Unified HTTP error mapping (`LLMErrorMapper`)

* The rules that translate HTTP status codes into `LLMError` live in a single place, `LLMErrorMapper`, shared by the official SDK path (`ClientResultException`) and the embedding service.
* `Retry-After` parsing lives there too, built on `RetryConditionHeaderValue`, so both the delay-seconds and HTTP-date forms are handled identically everywhere.
* As a result, "which status codes are retryable" and "which indicate a rejected schema that should be downgraded" behave identically everywhere. Third-party custom providers can reference the same mapper.

### 5. Fault-tolerant structured output (structured output & JSON repair)

* Developers frequently need the model to return a specific JSON shape.
* The built-in OpenAI and Gemini providers prefer the official SDKs' native structured output: OpenAI through `IChatClient`'s JSON Schema response format, Gemini through `ResponseMimeType = "application/json"` plus `ResponseSchema`. The framework validates required members and null state before deserializing into the target C# object.
* The schema itself is generated per provider dialect — all members land in `required`, and optionality is expressed as a `["integer","null"]` union (OpenAI) or `nullable: true` (Gemini). See [Architecture §6](#6-official-sdks-and-provider-responsibilities).
* The `RepairJson` fallback is only used when the provider has no native schema support, the service rejects the schema, or the model still returns malformed output. It handles Markdown fences (such as ` ```json `), unclosed brackets, trailing commas and JSON block extraction.

### 6. Official SDKs and provider responsibilities

* The main project and the test project stay on `net472`; RimWorld mods are not required to move to .NET 8. The official SDKs' dependency DLLs ship with the mod and are verified loadable by a startup compatibility gate. Although .NET Framework treats `System.ValueTuple` as a framework assembly, the build explicitly deploys its `4.0.5.0` DLL to avoid a `ReflectionTypeLoadException` when RimWorld's Mono reflects over MEAI.
* **OpenAI** uses the `OpenAI` SDK `2.12.0` together with `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` `10.8.3`. The built-in `OpenAIProvider` enters the shared manager through `ChatClient.AsIChatClient()`. Only endpoints that genuinely implement the OpenAI Chat Completions protocol (LM Studio, Ollama, vLLM, …) are suitable for the OpenAI-compatible adapter.
* **Gemini** uses the official `Google.GenAI` `1.17.0`, building a Gemini Developer API client from an API key. Text, streaming, native schema, thinking, context cache and safety settings all go through the native `Google.GenAI` path (isolated in code behind test seams: `CreateGenAiClient`, `GenerateContentNativeAsync`, `GenerateContentStreamNativeAsync`, `CreateCachedContentNativeAsync`). Gemini is never emulated with `OpenAI.Chat.ChatClient`.
* **Every built-in provider goes through an official SDK**: the OpenAI family (OpenAI, OpenRouter, DeepSeek, Groq, Grok, Z.ai, Kimi, MiniMax, Qwen, NVIDIA, OpenAICompatible) uses the `OpenAI` SDK `2.12.0` plus MEAI's `IChatClient`; Gemini uses the native `Google.GenAI` path. Model listings use `OpenAIModelClient.GetModelsAsync()` rather than hand-rewriting the `/models` URL and parsing JSON.
* **There is no raw HTTP path left anywhere.** Creating Gemini `cachedContents` explicit caches was the last one; it now goes through `Client.Caches.CreateAsync`, which returns a typed `CachedContent` (so `ExpireTime` needs no string parsing). An earlier note in this file claimed `Caches` exposed only `ListAsync` — that was wrong and had never been verified; reflection over the shipped assembly shows `CreateAsync`, `GetAsync`, `UpdateAsync`, `DeleteAsync` and `ListAsync` are all public. Removing that path deleted the framework's entire HTTP transport and its auth-header handling.
* **JSON Schema generation is `System.Text.Json`'s `JsonSchemaExporter` plus a normalization layer** (`RimLLMSchemaBuilder`), in three stages. **Stage A** exports full JSON Schema through the exporter. **Stage B** normalizes it into a restricted subset every provider accepts: `$ref` pointers are resolved and inlined, cycles and over-deep nesting are truncated, nullable unions collapse to a single `type`, and only a keyword whitelist survives. **Stage C** applies the target provider's dialect. Two dialects exist, selected from `LLMProviderCapabilities.PreferredSchemaProfile` so third-party providers can declare their own: OpenAI expresses optional members as `["integer","null"]` unions, Gemini as a single `type` plus `nullable: true`.
  * **The exporter is called directly rather than through MEAI's `AIJsonUtilities.CreateJsonSchema` wrapper.** That wrapper ships as a `net462` asset referencing `System.ComponentModel.DataAnnotations` (it reads `[EmailAddress]`, `[Range]` and friends to enrich the schema). RimWorld's Mono BCL does not contain that assembly, so in-game the call throws `TypeLoadException: Could not resolve type … 'EmailAddressAttribute' in assembly 'System.ComponentModel.DataAnnotations, Version=4.0.0.0'` and schema generation silently falls back to the legacy generator — invisible to unit tests, which run on a real .NET Framework that has it in the GAC. `System.Text.Json` carries no such reference and is the same engine MEAI uses internally, so calling it directly costs nothing. The one thing MEAI adds that is still wanted, `[Description]`, is read by Stage B itself. `SchemaGenerationEngineHasNoDataAnnotationsDependency` pins this.
  * Two consequences of using the exporter directly: it emits `{"enum":[…]}` with no `type` keyword for enums (Stage B infers the type from the enum values, otherwise every enum member would vanish), and it has no `description` concept at all (Stage B reads `[Description]` on both members and types).
  * **Cycles are cut at the CLR type level, not at the JSON pointer level.** The exporter expands a recursive member one full round before emitting the `$ref` back to the ancestor, so pointer-based detection ships an extra layer — measured at 789 → 3119 characters for the recursive test type, paid in prompt tokens on every request. `RecursiveSchemaStaysCompact` guards this.
  * **The nesting limit is per-dialect.** OpenAI's strict structured output allows at most 5 levels of nesting (and 100 object properties in total); exceeding it gets the schema rejected and silently downgraded to prompt-based JSON, so the OpenAI dialect truncates at 5. Gemini has no such limit and keeps the framework-wide limit of 8. Note the 100-property cap is **not** enforced yet.
  * Raw exporter output cannot be sent as-is. `Google.GenAI.Types.Schema.Type` is a single enum value, so a union type makes `Schema.FromJson` **silently return null** — Gemini then receives no schema at all, with no error surfacing anywhere. This is pinned by a pair of regression tests (`RawMeaiSchemaIsRejectedByGoogleSchemaFromJson` and `GeminiProfileSchemaIsAcceptedByGoogleSchemaFromJson`) rather than left as a claim in this document.
  * `$ref` is **not** only used for recursion — MEAI also emits it to deduplicate a repeated type, so blanket-truncating every `$ref` would silently delete ordinary members. The normalizer resolves the JSON pointer and only treats it as a cycle when it points at an ancestor on the current expansion path.
  * **Every member is listed in `required`**; optionality is carried by the type instead. OpenAI's strict structured output requires `required` to cover all properties, so the previous behaviour (leaving `Nullable<T>` out of `required` while still sending `strict: true`) was rejected server-side and silently downgraded to prompt-based JSON.
  * Because the schema comes from System.Text.Json's exporter while deserialization is Newtonsoft's, a contract modifier aligns the two (fields included, `[JsonIgnore]` honoured, `[JsonProperty]` names applied, read-only members dropped), and a test asserts the member sets match. **Do not use a custom Newtonsoft `JsonConverter` on structured-output types** — it changes the wire shape in a way the exporter cannot see.
  * If `JsonSchemaExporter` is ever unavailable in RimWorld's Mono runtime, the builder logs a warning, permanently falls back to the previous reflection implementation, and forces `strict` off.
* Provider-specific SDKs never appear in `RimLLMManager` or the public SDK façade; the shared layer depends only on `IChatClient`, `LLMProviderCapabilities` and the existing `ILLMProvider` API. API keys always come from the encrypted settings and are never written into source code or ordinary logs.

---

## 🔐 Security notes

To avoid misunderstanding, here is an honest description of what each security mechanism actually protects against:

* **API key encryption is obfuscation-grade protection.** Keys are AES-256 encrypted in the settings file, with the encryption key derived from a fixed seed and the device identifier (`deviceUniqueIdentifier`). This prevents plaintext from being read after the settings file is copied to another machine, and avoids accidental leaks when syncing or sharing settings — but it **cannot** defend against code running on the same machine (including other mods), because the encryption logic and material live in the same process and a determined attacker can recover the plaintext. Treat it as protection against mistakes and accidental disclosure, not as a safe.
* **There is no caller verification, by design.** Earlier versions bound each `modId` to a calling assembly. That check could not stop a hostile mod — everything runs in one process, so reflection defeats it — and it was first-come-wins, meaning a mod loading earlier could squat an id and make the legitimate owner throw at startup. It was removed: it converted a negligible spoofing risk into a real denial-of-service one.
* **Keys never reach URLs or logs.** All providers pass keys via HTTP headers; log output always goes through `SanitizeForLog` and is length-truncated, and the device identifier is masked in diagnostic exports.

---

## 📜 License

This mod's source code is released under the **MIT License** — Copyright (c) 2026 **mushroomTW**. See [LICENSE](LICENSE).

Redistributed dependency assemblies in `Assemblies/` keep their own licenses: Microsoft.Extensions.AI, the OpenAI .NET SDK and Newtonsoft.Json are MIT; Google.GenAI and Google.Apis.\* are Apache-2.0. RimWorld's own assemblies belong to Ludeon Studios and are not redistributed here.

---

## 🧪 Unit tests and verification

The project ships with a full unit test suite in `Source/RimLLM Framework.Tests` (a standalone project alongside the main one) covering AES encryption/decryption, model fallback, JSON Schema generation (normalization, per-provider dialects, and a paired comparison against `Google.GenAI`'s `Schema.FromJson`) and repair, HTTP error mapping, `Retry-After` parsing, `ChatOptions` cloning, streaming retries and budget control.

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

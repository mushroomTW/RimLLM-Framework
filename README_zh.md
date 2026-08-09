# RimLLM Framework

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[English](README.md)

`RimLLM Framework` 是一個為 RimWorld Mod 提供大型語言模型（LLM）呼叫介面與核心基礎建設的底層框架，讓其他 RimWorld AI Mod 有一個穩健、方便、高效且開箱即用的 SDK，不必重造輪子。

框架回傳的一切都是**標準的 Microsoft.Extensions.AI 型別** —— `IChatClient`、`ChatMessage`、`ChatResponse`、`IEmbeddingGenerator`，沒有另一套自訂 client 介面要學。

---

## 📦 安裝

你的 Mod 需要在**編譯期**取得 Microsoft.Extensions.AI（MEAI）型別，但**執行期不可以自己帶一份**。RimLLM Framework 已經把所有 MEAI DLL 部署在自己的 `Assemblies/` 資料夾裡，而 RimWorld 會把所有 Mod 載入同一個 AppDomain —— 多一份複本就會產生兩個彼此不相容的 `IChatClient` 型別，任何轉型都會失敗。

下面兩種做法的原則相同：**只參考，不複製。**

### 方案 A —— NuGet（建議）

```xml
<ItemGroup>
  <!-- IChatClient / ChatMessage / ChatResponse / IEmbeddingGenerator。
       ExcludeAssets="runtime" 保留參考但不把 DLL 複製到你的 Assemblies 資料夾。 -->
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.8.3" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.8.3) —— 使用端 Mod 只需要這一個。它會帶進 `Microsoft.Extensions.AI.Abstractions`，`IChatClient` 就在裡面。
* [`Microsoft.Extensions.AI.OpenAI` 10.8.3](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.8.3) —— 框架另外會一併發佈這一顆。只有在你要自己建構 OpenAI SDK 用戶端時才需要參考；單純呼叫 `RimLLMProvider.CreateChatClient` 的 Mod 不需要。

> [!IMPORTANT]
> **版本必須釘死在 `10.8.3`。** 組件識別必須與框架載入的那一份完全一致。使用端 Mod 也不要把 `CopyLocalLockFileAssemblies` 設成 `true` —— 那正是造成上述 DLL 重複問題的原因。

框架本身的組件不在 NuGet 上，那部分請看方案 B。

### 方案 B —— 直接參考 DLL

直接從已安裝的框架 Mod 參考 DLL。`<Private>false</Private>` 是阻止 MSBuild 把它們複製到你輸出目錄的關鍵。

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

若 RimWorld 不在 Steam 預設路徑，請自行調整 `RimLLMDir`。

### 載入順序

在你的 Mod 的 `About/About.xml` 宣告相依，確保框架先初始化：

```xml
<loadAfter>
  <li>GreenMushroom.RimLLMFramework</li>
</loadAfter>
```

---

## 💻 SDK 使用方式

### 1. 引入命名空間

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;
```

### 2. 文字生成

`RimLLMProvider.CreateChatClient` 回傳標準的 MEAI `IChatClient`，框架的 Fallback 鏈、請求佇列、防濫用檢查與用量統計會自動套用。不需要註冊步驟 —— 傳入的 `modId` 只是一個標籤，用於每個 Mod 的節流與遙測歸屬：

```csharp
public async void AskSomething()
{
    try
    {
        // 取得綁定你這個 Mod 的 IChatClient。任何非空字串都可以，取一個獨特的即可。
        IChatClient chat = RimLLMProvider.CreateChatClient("myai.mod");

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, "你是一個冷酷、隨機且難以預測的說書人。"),
            new ChatMessage(ChatRole.User, "用 RimWorld 隨機蘭迪的口吻向玩家打招呼。")
        };

        // 未指定 ModelId 時，會使用玩家設定的 Fallback 鏈
        ChatResponse response = await chat.GetResponseAsync(messages);
        Log.Message($"[RandySays] {response.Text}");
    }
    catch (RimLLMException ex)
    {
        Log.Error($"[MyAIMod] 生成失敗，錯誤碼：{ex.Error}，訊息：{ex.Message}");
    }
}
```

要調整優先權或傳入進階設定時，使用 `RimLLMChatOptions`：

```csharp
ChatResponse response = await chat.GetResponseAsync(
    messages,
    new RimLLMChatOptions
    {
        Priority = 5,
        Temperature = 0.7f,
        MaxOutputTokens = 150
    });
```

### 3. 結構化輸出

定義你的 C# 資料類別，然後使用 `GetResponseObjectAsync<T>` 擴充方法：

```csharp
// 1. 定義期望的輸出結構
public class PawnIncidentDecision
{
    public string EventType; // "Good" 或 "Bad"
    public string IncidentDefName; // 例如 "RaidEnemy"
    public float Probability;
    public string RandyReasoning; // 說書人的思路
}

public async void MakeIncidentDecision()
{
    try
    {
        IChatClient chat = RimLLMProvider.CreateChatClient("myai.mod");

        // 框架會產生 JSON Schema、送出請求、
        // 修復格式錯誤的回應，並反序列化為目標物件
        PawnIncidentDecision decision = await chat.GetResponseObjectAsync<PawnIncidentDecision>(
            new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "你是一個專注於製造戲劇衝突的決策引擎。"),
                new ChatMessage(ChatRole.User, "分析殖民地現況，決定下一個事件類型與 DefName。")
            });

        Log.Message($"蘭迪選擇的事件：{decision.IncidentDefName}（{decision.RandyReasoning}）");
    }
    catch (RimLLMException ex)
    {
        Log.Error($"結構化決策生成失敗：{ex.Message}");
    }
}
```

### 4. 串流

使用標準的 MEAI `GetStreamingResponseAsync` 迭代 `ChatResponseUpdate` 串流：

```csharp
public async void StreamResponse()
{
    try
    {
        IChatClient chat = RimLLMProvider.CreateChatClient("myai.mod");

        var updates = chat.GetStreamingResponseAsync(
            new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "寫一段殖民地廣播稿。")
            });

        await foreach (ChatResponseUpdate update in updates)
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                // 可安全更新 UI（更新已派送回 Unity 主執行緒）
                MyGameUI.AppendText(update.Text);
            }
        }
    }
    catch (RimLLMException ex)
    {
        Log.Error($"串流生成失敗：{ex.Message}");
    }
}
```

> 整條 Fallback 鏈都失敗時，原始的 `RimLLMException` 會從 `await foreach` 重新擲出，
> 上面的 `catch` 必定會收到錯誤 —— 失敗的串流不會靜默結束。chunk 一產生就送出，中間沒有輪詢延遲。

### 5. 上下文快取以節省 Token

如果你的 Mod 有很大、穩定、且**在短時間內重複使用**的上下文（世界觀規則、固定角色背景、輸出 Schema 等），而且呼叫頻率高，可以透過 `RimLLMChatOptions.CachedContext` 啟用**上下文快取**：

```csharp
public async void CallWithCaching()
{
    IChatClient chat = RimLLMProvider.CreateChatClient("myai.mod");

    var options = new RimLLMChatOptions
    {
        // 大型、穩定、可重複使用的資料（規則書／Schema／固定設定）
        CachedContext = BuildAnalysisRulesAndOutputSchema()
    };

    var messages = new List<ChatMessage>
    {
        new ChatMessage(ChatRole.System, "你是 RimWorld 心理分析師。"),
        new ChatMessage(ChatRole.User, "根據以下殖民地狀態，分析每個成員的心理健康：" + BuildColonySnapshot())
    };

    ChatResponse response = await chat.GetResponseAsync(messages, options);
    Log.Message(response.Text);
}
```

> [!NOTE]
> 只要 `CachedContext` 不為空，`EnableContextCaching` 就會自動設為 `true`。
> **Gemini** 會快取 `SystemPrompt + CachedContext`（TTL 300 秒）。內容太小時（Pro 少於 2048 字元、其他模型少於 1024 字元），框架會退回一般的 `systemInstruction`，避免付了建立費卻沒有效益。
> **OpenAI** 會在服務端自動對重複前綴套用 prompt caching。

### 6. Embedding 向量

使用 `RimLLMProvider.CreateEmbeddingGenerator` 取得標準的 `IEmbeddingGenerator<string, Embedding<float>>`：

```csharp
public async void GenerateVector()
{
    var generator = RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
    GeneratedEmbeddings<Embedding<float>> result = await generator.GenerateAsync(new[] { "殖民者精神崩潰" });
    ReadOnlyMemory<float> vector = result[0].Vector;
}
```

Embedding 供應商預設為**停用**；玩家在設定中選擇供應商之前，`GenerateAsync` 會擲出 `RimLLMException`。若你需要一個完全不必 API 的替代方案，可使用獨立的 Trigram 工具 —— 它是單純的字串相似度函式，不受 Embedding 供應商設定影響：

```csharp
float similarity = RimLLMEmbeddingService.CalculateTrigramSimilarity("殖民者精神崩潰", "小人情緒失控了");
```

---

## 📖 功能特色

1. **多供應商支援**
   * 原生支援 Google **Gemini**、**OpenAI**、**DeepSeek**、**Groq**、**Grok (xAI)**、**Z.ai**、**OpenRouter**、**Kimi**、**MiniMax**、**Qwen** 與 **NVIDIA**。
   * 支援 **OpenAI 相容 API**，可設定任何本地或第三方相容端點（LM Studio、Ollama、LocalAI、vLLM 等）。預設端點為 `http://localhost:1234/v1`，並支援 API 金鑰。
   * **Kimi**、**MiniMax**、**Qwen** 提供一鍵切換「使用中國專用端點」（預設關閉），以改善連線品質。
2. **容錯與模型 Fallback**
   * **客戶端 Fallback 鏈**：可設定由主要模型與多個精確備援模型組成的鏈。目前模型遇到逾時、速率限制（HTTP 429）或連線錯誤時，框架會無縫往下切換。UI 產生的項目為 `Provider:Model` 形式；框架仍相容只填供應商的舊項目，並使用該供應商的預設模型。
   * **OpenRouter 服務端自動 Fallback（`openrouter/auto`）**：可把 OpenRouter 官方的 `openrouter/auto` 模型放進 Fallback 鏈，交由 OpenRouter 在服務端從推薦模型中挑選。
   * `Retry-After` 在所有路徑上都支援 RFC 7231 允許的兩種格式 —— 延遲秒數與 HTTP 日期。
3. **AES-256 設定加密**
   * API 金鑰以 AES-256 對稱加密儲存，降低設定檔中出現明文金鑰的風險。這是混淆等級的保護 —— 詳見下方[安全性說明](#-安全性說明)。
   * 所有供應商（含 Gemini）都以 HTTP Header 傳遞金鑰，絕不放在請求 URL，避免金鑰進入代理或伺服器的存取日誌。
   * RimWorld 的所有 Mod 都在同一個遊戲行程內執行。本框架不宣稱能阻止惡意 Mod 讀取記憶體、對公開 API 使用反射，或以其他行程內手段繞過邊界。
4. **精緻的可捲動多欄 GUI**
   * 直覺的模型 chip 流式格線，選取項目高亮，完整模型名稱以 tooltip 顯示。
5. **獨立除錯分頁與日誌開關**
   * 獨立的**除錯**設定分頁，含「詳細日誌」核取方塊，讓 Mod 開發者與玩家在排查問題時自由開關本 Mod 的日誌輸出。
6. **一鍵連線測試**
   * 即時連線檢查，量測延遲並驗證 API 金鑰與模型。在基底類別實作一次，所有供應商共用。
7. **執行緒安全與主執行緒 Scribe 派送**
   * 所有設定字典皆以鎖保護，防止多執行緒並發讀寫。
   * `RecordLog` 觸發的 Scribe 寫入會透過 `RimLLMDispatcher` 派送回 Unity 主執行緒，並套用 15 秒寫入節流，避免背景存檔造成崩潰或 TPS 掉幀。
8. **推理模型與思維鏈標記**
   * 原生支援 **DeepSeek-R1**、**Gemini 2.0/2.5 Thinking**、**OpenAI o1/o3** 等推理模型。
   * 框架會擷取 API 回傳的思維鏈（OpenAI 協定的 `reasoning_content`、Gemini 的 `thought` 欄位），並統一以 `<think>...</think>` 標籤包裹。
   * GUI 對話測試頁會解析這些標籤，將思維鏈以灰色斜體呈現。呼叫端 Mod 可用正規表示式輕易剝除或保留思維鏈。
   * **推理強度控制**：預設為「自動」，讓各供應商執行自己的自適應或動態思考設定（Gemini 的 `thinkingBudget = -1`、OpenAI 的動態 `reasoning_effort` 等）。也可以完全關閉推理，或手動設為低／中／高。
9. **上下文快取與 Prompt 快取**
   * 原生支援 **Gemini context caching** 與 **OpenAI prompt caching**。在 `RimLLMChatOptions` 設定 `CachedContext`，框架會提交 `SystemPrompt + CachedContext` 進行快取，大幅降低高頻重複請求的輸入 Token 成本與延遲。
   * **成本防呆**：Gemini 顯式快取有最小尺寸門檻，內容過小時框架會跳過快取改用 `systemInstruction`，避免建立費永遠回收不了。同一份上下文的快取建立也以鎖序列化，防止產生重複資源。
   * **量化節省**：用量統計會解析 API 回傳的快取命中 Token（OpenAI `cached_tokens`、Gemini `cachedContentTokenCount`）並套用折扣費率估算成本，讓成本面板反映真實節省。
10. **Embedding SDK**
    * 框架公開由 Google、Ollama 或 OpenAI 相容端點支援的 embedding 功能，並附餘弦相似度工具。其他 Mod 可透過 `RimLLMProvider.CreateEmbeddingGenerator` 取得標準 `IEmbeddingGenerator`，用於語意檢索、分群或相似度比對。
    * 三種線上來源全走官方 SDK：Google 使用 `Google.GenAI` 的 `EmbedContentAsync`；Ollama 與自架服務使用 OpenAI SDK 的 `EmbeddingClient`（Ollama 走其 OpenAI 相容的 `/v1` 端點）。因此「Embedding 端點」欄位填的是**服務根位址**（如 `http://localhost:11434/v1`）；填入完整 `/embeddings` 路徑會自動正規化。
    * `CalculateTrigramSimilarity` 是**獨立的**、不需 API 的字串相似度工具，**不是** Embedding 供應商。它不產生向量，且無論選擇哪個供應商（或不選）都能呼叫。
    * Embedding 屬計費 API，因此與一般生成請求共用同一套防濫用檢查；其金鑰採用與供應商金鑰相同的 AES 加密。

---

## 🛠️ 架構設計

### 1. 統一介面與調度核心（`IChatClient` / `IEmbeddingGenerator` 與 `RimLLMProvider`）

* 框架對外暴露標準的 Microsoft.Extensions.AI 介面。呼叫端只面對 `IChatClient` 或 `IEmbeddingGenerator`，完全不需要知道實際由哪個供應商或模型處理 —— 調度與 Fallback 輪替由 `RimLLMManager` 負責。
* 具體的 facade（`RimLLMChatClient`、`RimLLMEmbeddingClient`）為 `internal`。使用端會碰到的框架專屬型別只有 `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException` 與 `LLMError`，其餘跨越邊界的全是 MEAI 型別。
* `modId` 是純標籤，不是憑證。它是每個 Mod 防濫用節流與遙測歸屬的鍵，不需要任何註冊呼叫。

### 2. Unity 主執行緒派送器（`RimLLMDispatcher`）

* 網路請求在背景執行緒池上非同步執行，但多數 Unity API 與 RimWorld 邏輯並非執行緒安全 —— 從背景執行緒呼叫會造成崩潰或 TPS 掉幀。
* `RimLLMDispatcher` 是 MonoBehaviour 單例，以 `ConcurrentQueue` 收集背景執行緒的回呼，並在 Unity 每幀的 `Update` 中派送回主執行緒。

### 3. 串流橋接（`Channel<T>`）

* Manager 的串流 API 是回呼形式（`Action<string> onChunkReceived`），而 MEAI 要的是 `IAsyncEnumerable<ChatResponseUpdate>`。兩者之間以無界的 `System.Threading.Channels.Channel<T>` 橋接，消費端就是 `ChannelReader.ReadAllAsync()`。
* 由於 `IAsyncEnumerable` 是透過 `bclasync` extern alias 進入本專案，C# 8 無法對它編譯 async iterator。`ReadAllAsync()` 直接繞過這個限制：它回傳的正是同一顆組件的 `IAsyncEnumerable`，因此不必手寫任何 iterator。
* 一層薄包裝會解開 `ChannelClosedException`，讓生產端的失敗以原始的 `RimLLMException` 呈現給呼叫端。

### 4. 統一的 HTTP 錯誤對照（`LLMErrorMapper`）

* HTTP 狀態碼轉換為 `LLMError` 的規則集中在 `LLMErrorMapper` 一處，由官方 SDK 路徑（`ClientResultException`）與 embedding 服務共用。
* `Retry-After` 的解析也在其中，基於 `RetryConditionHeaderValue`，因此延遲秒數與 HTTP 日期兩種格式在各處行為一致。
* 這使得「哪些狀態碼可重試」與「哪些代表 Schema 遭拒應降級」在各處行為完全相同。第三方自訂供應商也能引用同一份對照。

### 5. 容錯的結構化輸出（structured output 與 JSON repair）

* 開發者經常需要模型回傳特定的 JSON 結構。
* 內建的 OpenAI 與 Gemini 供應商優先使用官方 SDK 的原生結構化輸出：OpenAI 透過 `IChatClient` 的 JSON Schema response format，Gemini 透過 `ResponseMimeType = "application/json"` 加 `ResponseSchema`。框架會先驗證必要成員與 null 狀態，再反序列化為目標 C# 物件。
* `RepairJson` 回退機制僅在供應商不支援原生 Schema、服務拒絕 Schema，或模型仍回傳格式錯誤內容時啟用。它處理 Markdown 圍籬（如 ` ```json `）、未閉合括號、尾隨逗號與 JSON 區塊擷取。

### 6. 官方 SDK 與供應商職責

* 主專案與測試專案維持 `net472`；RimWorld Mod 不需要遷移到 .NET 8。官方 SDK 的相依 DLL 隨 Mod 發佈，並由啟動相容性檢查確認可載入。雖然 .NET Framework 將 `System.ValueTuple` 視為框架組件，建置仍明確部署其 `4.0.5.0` DLL，以避免 RimWorld 的 Mono 反射 MEAI 時發生 `ReflectionTypeLoadException`。
* **OpenAI** 使用 `OpenAI` SDK `2.12.0` 搭配 `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` `10.8.3`。內建的 `OpenAIProvider` 透過 `ChatClient.AsIChatClient()` 進入共用 manager。只有真正實作 OpenAI Chat Completions 協定的端點（LM Studio、Ollama、vLLM…）才適合 OpenAI 相容轉接。
* **Gemini** 使用官方 `Google.GenAI` `1.17.0`，以 API 金鑰建立 Gemini Developer API 用戶端。文字、串流、原生 Schema、思考、上下文快取與安全設定全走原生 `Google.GenAI` 路徑（在程式碼中以測試縫隔離：`CreateGenAiClient`、`GenerateContentNativeAsync`、`GenerateContentStreamNativeAsync`、`CreateCachedContentNativeAsync`）。Gemini 絕不以 `OpenAI.Chat.ChatClient` 模擬。
* **每個內建供應商都走官方 SDK**：OpenAI 家族（OpenAI、OpenRouter、DeepSeek、Groq、Grok、Z.ai、Kimi、MiniMax、Qwen、NVIDIA、OpenAICompatible）使用 `OpenAI` SDK `2.12.0` 加 MEAI 的 `IChatClient`；Gemini 走原生 `Google.GenAI` 路徑。模型清單使用 `OpenAIModelClient.GetModelsAsync()`，而非自行拼 `/models` URL 再解析 JSON。
* **框架已無任何 raw HTTP 路徑。** 建立 Gemini `cachedContents` 顯式快取是最後一處，現已改走 `Client.Caches.CreateAsync`，回傳型別化的 `CachedContent`（`ExpireTime` 直接是 `DateTime?`，不需要再解析字串）。本文件先前宣稱 `Caches` 只暴露 `ListAsync` —— 那是錯的，而且從未被驗證過；對實際組件反射顯示 `CreateAsync`、`GetAsync`、`UpdateAsync`、`DeleteAsync`、`ListAsync` 全是公開成員。移除該路徑後，整個 HTTP 傳輸層與認證 Header 處理都一併刪除。
* **JSON Schema 產生刻意不使用 `AIJsonUtilities.CreateJsonSchema`。** MEAI 產出的是完整 JSON Schema —— 可為 null 的成員會變成 `"type": ["string","null"]` 聯集型別，遞迴型別則以 `$ref` 表達。實測顯示三種測試型別的輸出全都被 `Google.GenAI` 的 `Schema.FromJson` 拒絕。`RimLLMJsonHelper` 改為產出所有供應商都接受的受限子集（單一 `type`、遞迴成員截斷）。兩者解決的是不同問題。
* 供應商專屬 SDK 絕不出現在 `RimLLMManager` 或公開 SDK facade 中；共用層只相依 `IChatClient`、`LLMProviderCapabilities` 與既有的 `ILLMProvider` API。API 金鑰一律來自加密設定，絕不寫入原始碼或一般日誌。

---

## 🔐 安全性說明

為避免誤解，以下誠實說明每項安全機制實際防護的範圍：

* **API 金鑰加密屬混淆等級保護。** 金鑰在設定檔中以 AES-256 加密，加密金鑰由固定種子與裝置識別碼（`deviceUniqueIdentifier`）衍生。這能防止設定檔被複製到其他機器後被讀出明文，也避免同步或分享設定時意外外洩 —— 但**無法**防禦在同一台機器上執行的程式碼（包含其他 Mod），因為加密邏輯與素材都在同一個行程內，有心人可還原明文。請把它視為防呆與防止意外揭露，而不是保險箱。
* **刻意不做呼叫者驗證。** 舊版會把每個 `modId` 綁定到一個呼叫端組件。這個檢查擋不住惡意 Mod —— 全部都在同一行程內，反射就能繞過 —— 而且是先到先贏，載入較早的 Mod 可以占用某個 id，讓正牌擁有者在啟動時直接擲出例外。因此移除：它把可忽略的偽造風險換成了真實的阻斷服務風險。
* **金鑰不會進入 URL 或日誌。** 所有供應商都以 HTTP Header 傳遞金鑰；日誌輸出一律經過 `SanitizeForLog` 並截斷長度，診斷匯出中的裝置識別碼也會遮罩。

---

## 🧪 單元測試與驗證

專案在 `Source/RimLLM Framework.Tests`（與主專案並列的獨立專案）附有完整的單元測試套件，涵蓋 AES 加解密、模型 Fallback、JSON Schema 產生與修復、HTTP 錯誤對照、`Retry-After` 解析、`ChatOptions` 複製、串流重試與預算控制。

> **前置需求**：測試在執行期需要 RimWorld 的 `Assembly-CSharp` 與 Unity DLL。這些檔案不可轉散布，因此需要本機安裝 RimWorld。
> 預設路徑為 `C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed`，
> 可用 MSBuild 屬性 `RimWorldManagedDir` 或環境變數 `RIMWORLD_MANAGED_DIR` 覆寫。

在專案根目錄以 `dotnet` CLI 建置與測試：

```bash
# 還原並重新建置方案
dotnet build "Source/RimLLM Framework.slnx"

# 執行所有 NUnit 單元測試
dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"
```

> **注意**：`Krafs.Rimworld.Ref` 參考組件不會限制 BCL 表面，因此有可能寫出「編譯得過但在 RimWorld 的 Mono
> 執行期失敗」的程式碼。已知案例：`Stack<T>` 會擲出 `TypeLoadException`，而無參數的 `String.TrimEnd()`
> 多載並不存在。請務必以實際的 `dotnet test` 驗證，不要只依賴建置成功。

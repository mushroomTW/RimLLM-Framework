# RimLLM Framework

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20繁中%20%7C%20简中-orange.svg)

[English](README.md)

`RimLLM Framework` 是一個為 RimWorld Mod 提供大型語言模型（LLM）呼叫介面與核心基礎建設的底層框架，讓其他 RimWorld AI Mod 有一個穩健、方便、高效且開箱即用的 SDK，不必重造輪子。

框架回傳的一切都是**標準的 Microsoft.Extensions.AI 型別** —— `IChatClient`、`ChatMessage`、`ChatResponse`、`IEmbeddingGenerator`，沒有另一套自訂 client 介面要學。

```csharp
// 呼叫端 API 的全貌。供應商、模型、API 金鑰與 Fallback 鏈
// 都已經由玩家在本 Mod 的設定介面裡配置好。
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

> [!IMPORTANT]
> 本框架是相依項，不是獨立功能。執行期玩家必須同時啟用 **RimLLM Framework** 這個 Mod，
> 並且已經設定好至少一個帶 API 金鑰的供應商；否則 `RimLLMProvider.CreateChatClient` 會擲出
> `InvalidOperationException`，呼叫也會以 `RimLLMException` 失敗。
> 支援的遊戲版本：**RimWorld 1.6**（見 `About/About.xml`）。

## 目錄

* [安裝](#-安裝) —— 只參考組件，不重複出貨
* [SDK 使用方式](#-sdk-使用方式) —— 對話、串流、結構化輸出、Embedding、錯誤處理
* [功能特色](#-功能特色) —— 框架替你做掉了什麼
* [架構設計](#️-架構設計) —— 怎麼做到的，以及為什麼這樣做
* [安全性說明](#-安全性說明) —— 每個機制實際防得住什麼
* [授權條款](#-授權條款) · [單元測試與驗證](#-單元測試與驗證)

---

## 📦 安裝

你的 Mod 需要在**編譯期**取得 Microsoft.Extensions.AI（MEAI）型別，但**執行期不可以自己帶一份**。RimLLM Framework 已經把所有 MEAI DLL 部署在自己的 `Assemblies/` 資料夾裡，而 RimWorld 會把所有 Mod 載入同一個 AppDomain —— 多一份複本就會產生兩個彼此不相容的 `IChatClient` 型別，任何轉型都會失敗。

下面兩種做法的原則相同：**只參考，不複製。**

### 方案 A —— NuGet（建議）

```xml
<ItemGroup>
  <!-- IChatClient / ChatMessage / ChatResponse / IEmbeddingGenerator。
       ExcludeAssets="runtime" 保留參考但不把 DLL 複製到你的 Assemblies 資料夾。 -->
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.9.0" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.9.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.9.0) —— 使用端 Mod 只需要這一個。它會帶進 `Microsoft.Extensions.AI.Abstractions`，`IChatClient` 就在裡面。
* [`Microsoft.Extensions.AI.OpenAI` 10.9.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.9.0) —— 框架另外會一併發佈這一顆。只有在你要自己建構 OpenAI SDK 用戶端時才需要參考；單純呼叫 `RimLLMProvider.CreateChatClient` 的 Mod 不需要。

> [!IMPORTANT]
> **版本必須釘死在 `10.9.0`。** 組件識別必須與框架載入的那一份完全一致。使用端 Mod 也不要把 `CopyLocalLockFileAssemblies` 設成 `true` —— 那正是造成上述 DLL 重複問題的原因。
>
> **從舊版框架升上來要注意：**MEAI 的組件版本是跟著 `major.minor` 走的，`10.8.3` 產生的是 `10.8.0.0`，`10.9.0` 產生的是 `10.9.0.0`。因此對著 `10.8.3` 編譯的使用端 Mod 必須把這行的版本改掉並重新編譯 —— 這不是原始碼層的破壞性變更，但舊的二進位已經對不上框架載入的那一份。

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

**如果你已經會用 [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.9.0)，你就已經會用這套 API。**

RimLLM Framework 的全部工作，就是交給你一個標準的 MEAI `IChatClient`。從那一行之後全是純 Microsoft.Extensions.AI，因此本文件只寫本框架專屬的部分；MEAI 本身的語法請看 [Microsoft 官方文件](https://learn.microsoft.com/zh-tw/dotnet/ai/microsoft-extensions-ai)。

### 只有一行不一樣

```csharp
// 由玩家在模組設定中提供供應商、模型、金鑰與 Fallback 鏈
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");
```

其他 provider 套件要你用模型名稱與 API 金鑰自行建構 client，這裡改成呼叫這一行。`"myai.mod"` 只是一個標籤，用於各 Mod 的節流與用量歸屬。你這邊不需要註冊呼叫，也不需要處理任何金鑰。

### 對話

```csharp
using Microsoft.Extensions.AI;
using RimLLM_Framework;

IChatClient client = RimLLMProvider.CreateChatClient("myai.mod");

Log.Message((await client.GetResponseAsync("What is AI?")).Text);
```

訊息清單與 `ChatOptions` 的用法與 MEAI 文件完全相同。唯一與本框架有關的規則是：不設定 `ModelId` 時，實際由哪個供應商與模型執行，交給玩家設定的 Fallback 鏈決定；要指定就填 `"供應商:模型"` 形式的項目。

回傳的 `ChatResponse` 就是供應商產生的那一個，除了 `ModelId` 被改寫成 `"供應商:模型"`（讓你在 failover 之後仍分辨得出實際是誰回的）之外，一律原樣交還。`ResponseId`、`CreatedAt`、`ConversationId`、`Usage`、`FinishReason`、`RawRepresentation` 與 `AdditionalProperties` 都是供應商設什麼就是什麼，包括 `null`。`Usage` 為 `null` 代表供應商沒有回報 token 數，不是這次呼叫免費。

### 串流

串流是標準 MEAI 的 `GetStreamingResponseAsync` / `await foreach`，語法見 MEAI 文件。框架額外保證兩件事：每一個 update 都已派送到 Unity 主執行緒，可以直接在迴圈裡操作 UI；整條 Fallback 鏈失敗時，原始的 `RimLLMException` 會從 `await foreach` 重新擲出，串流不會無聲結束。

### 結構化輸出

`GetResponseObjectAsync<T>` 是掛在 `IChatClient` 上的擴充方法。它會產生 JSON Schema、修復格式錯誤的輸出，並反序列化給你：

```csharp
public class PawnIncidentDecision
{
    public string EventType;       // "Good" 或 "Bad"
    public string IncidentDefName; // 例如 "RaidEnemy"
    public float Probability;
}

PawnIncidentDecision decision = await client.GetResponseObjectAsync<PawnIncidentDecision>(
    new List<ChatMessage>
    {
        new ChatMessage(ChatRole.User, "決定下一個事件類型與 DefName。")
    });
```

> [!NOTE]
> 請用這個而不是 MEAI 自己的 `GetResponseAsync<T>`。MEAI 的 `AIJsonUtilities.CreateJsonSchema` 在 RimWorld 的 Mono 環境根本無法執行（它會拉進該環境沒有的 `System.ComponentModel.DataAnnotations`），其原始 schema 形狀（聯集型別、`$ref`）也不被 Google Gemini 接受，而且 MEAI 沒有 JSON 修復路徑。RimLLM 驅動的是同一個底層 `JsonSchemaExporter`，但在其上加了正規化層與各供應商方言。詳見[架構設計 §6](#6-官方-sdk-與供應商職責)。

### 原生 Tool Calling（函式呼叫）

RimLLM Framework 原生支援 Microsoft.Extensions.AI 的 Tool Calling（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`）。OpenAI 與 Google Gemini 均支援完整的雙向工具 Schema 與訊息協定轉譯。

#### 1. 自動迴圈執行模式（推薦，內建 Unity 主執行緒安全調度）

在 RimWorld 中，工具委派通常需要存取遊戲地圖、Pawn 或遊戲世界物件。為了避免 Unity 跨執行緒 API 違規引發遊戲崩潰，請使用 `AsMainThreadFunctionInvokingClient()` 擴充方法包裝客戶端。這能確保工具的執行委派一律透過 `RimLLMDispatcher` 安全排入 Unity 主執行緒：

```csharp
// 包裝 client 以獲得自動多輪迴圈呼叫能力與主執行緒安全
IChatClient client = RimLLMProvider.CreateChatClient("myai.mod")
    .AsMainThreadFunctionInvokingClient(maxIterations: 10);

var weatherTool = AIFunctionFactory.Create(
    (string colonyName) => Find.CurrentMap.weatherManager.curWeather.label,
    "GetColonyWeather",
    "取得殖民地當前天氣");

var options = new ChatOptions
{
    Tools = new List<AITool> { weatherTool }
};

// 模型呼叫工具，RimLLM 在主執行緒執行該工具並將結果回傳給模型，直到產出最終回覆
ChatResponse response = await client.GetResponseAsync(
    new List<ChatMessage> { new ChatMessage(ChatRole.User, "目前我們殖民地的天氣如何？") },
    options);

Log.Message(response.Text);
```

#### 2. 手動單輪模式 (Raw Mode)

若你的 Mod 希望手動掌控每一輪，就不要套用上面那層包裝，直接把 `ChatOptions.Tools` 傳給 `client.GetResponseAsync()`。回應會帶著 `FunctionCallContent` 與 `FinishReason = ChatFinishReason.ToolCalls`，之後的迴圈完全依 MEAI 文件的標準寫法自行驅動。注意這條路徑不會有任何主執行緒派送 —— 那正是上面那層包裝存在的理由。

### Embedding 向量

形狀一樣：一行框架呼叫，之後全是標準 MEAI：

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
```

`GenerateAsync`、`GeneratedEmbeddings<T>` 與 `Embedding<float>` 的行為與 MEAI 文件相同。每個 `Embedding<float>` 都帶著實際算出它的 `ModelId`，`GeneratedEmbeddings.Usage` 則在供應商有回報時帶回輸入 token 數—— OpenAI 相容端點會回報，Gemini 公開 API 不會（它的 `tokenCount` 限 Enterprise 平台），因此在 Gemini 下 `Usage` 維持 `null`。與對話一樣，`null` 代表供應商沒有回報，不是這次呼叫免費。Embedding 供應商預設為**停用**；玩家選擇之前，`GenerateAsync` 會擲出 `RimLLMException`。

### 錯誤處理

所有失敗都以 `RimLLMException` 呈現，並帶有與供應商無關的 `LLMError` 代碼：

```csharp
try
{
    ChatResponse response = await client.GetResponseAsync(messages);
}
catch (RimLLMException ex) when (ex.Error == LLMError.QuotaExceeded)
{
    Messages.Message("API 額度已用盡。", MessageTypeDefOf.RejectInput, false);
}
catch (RimLLMException ex)
{
    Log.Error($"[MyAIMod] {ex.Error}：{ex.Message}");
}
```

`LLMError` 的值：`Timeout`、`RateLimit`、`InvalidKey`、`ProviderOffline`、`InvalidResponse`、`NetworkError`、`ModelNotFound`、`ContentFilter`、`QuotaExceeded`、`Cancelled`、`Unknown`、`ContextWindowExceeded`。

`ContentFilter` 與 `ContextWindowExceeded` 從籠統的 4xx 拒絕中拆出來，是因為三者的處置方式完全不同：縮短提示詞、換一家供應商，還是修請求。OpenAI 協定家族對這兩種情況一律回 `400`，因此由錯誤對照表依訊息內容判定；`413` 則依定義直接對應 `ContextWindowExceeded`。Schema、reasoning 與 temperature 遭拒仍維持回報 `InvalidResponse`，不影響現有的「去掉該參數重打一次」流程。新的列舉成員一律追加在 `LLMError` 末尾，已編譯的下游 Mod 數值不變。

### 唯一多出來的型別：`RimLLMChatOptions`

一般旋鈕用 `ChatOptions` 就夠了。只有在你需要 MEAI 沒有對應概念的功能時，才改用 `RimLLMChatOptions` —— 其餘設定的行為完全不變：

```csharp
var options = new RimLLMChatOptions
{
    Temperature = 0.7f,          // 這是原本的 ChatOptions
    Priority = 5,                // 數值越高，在全域請求佇列中越先執行
    CachedContext = worldRules,  // 大型可重用前綴，啟用供應商端的上下文快取
    MinFallbackLevel = "Medium", // 降級不得低於此模型等級
    DisableReasoning = true      // 對支援推理的模型關閉思考
};

ChatResponse response = await client.GetResponseAsync(messages, options);
```

`CachedContext` 不為空時，`EnableContextCaching` 會自動開啟。Gemini 會快取 `SystemPrompt + CachedContext`（TTL 300 秒），內容太小而不值得付建立費時則退回一般的 `systemInstruction`；OpenAI 則在服務端自動對重複前綴套用 prompt caching。

### 你不必自己寫的部分

這才是這個框架存在的意義。以下全部已經在那一個 `IChatClient` 後面完成：

| 你可以省略 | 因為框架已經做了 |
| --- | --- |
| API 金鑰的儲存與 UI | AES-256 加密設定，所有 Mod 共用同一份 |
| 挑選供應商或模型 | 玩家設定的 Fallback 鏈，項目為 `Provider:Model` 形式 |
| 重試與 `Retry-After` | 逾時／429／連線錯誤自動重試，兩種標頭格式都支援 |
| 供應商之間的容錯切換 | 回應開始輸出之前，自動沿 Fallback 鏈降級 |
| 處理掛掉的供應商 | 熔斷器，連續失敗後以指數退避冷卻 |
| 跨 Mod 的流量控制 | 全域優先佇列與並行上限，避免多個 Mod 同時打 API 造成掉幀 |
| 費用控管 | 每日預算，可選硬性阻擋／模擬回應／改用免費模型／詢問玩家 |
| 用量與費用回報 | Debug 分頁的各供應商 Token 與成本看板 |
| 推理模型的差異 | `reasoning_content` 與 Gemini 的 `thought` 統一正規化為 MEAI 的 `TextReasoningContent` |
| 格式錯誤的 JSON | 修復 Markdown 圍籬、未閉合括號與尾隨逗號，並具備 LLM 輔助的二次修復 |
| 主執行緒切換 | 串流 chunk 與日誌寫入都已派送回 Unity 主執行緒 |
| 原生 Tool Calling 與主執行緒排程 | Gemini／OpenAI 雙向工具轉譯；工具委派自動排入 Unity 主執行緒 |

### API 表面速查

`using RimLLM_Framework;` 會引入 14 個公開型別。多數 Mod 只會碰到第一列：

| 分層 | 型別 | 什麼時候需要 |
| --- | --- | --- |
| **呼叫模型** | `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException`、`LLMError`、`RimLLMClientExtensions`、`RimWorldFunctionInvoker` | 一定會用到 —— 這就是全部的使用端 API |
| **提供供應商** | `ILLMProvider`、`LLMProviderCapabilities`、`IRimLLMSettings` | 只有要用 `RimLLMProvider.RegisterProvider` 註冊自己的 LLM 後端時（`ILLMProvider` 直接產出標準 `Microsoft.Extensions.AI.IChatClient`） |
| **診斷** | `TestResult`、`ProviderIds`、`LLMErrorMapper` | 連線測試、內建供應商 ID 常數、HTTP 狀態碼對照 |

其餘的 `IChatClient`、`ChatMessage`、`ChatResponse`、`ChatResponseUpdate`、`IEmbeddingGenerator` 全都是 `Microsoft.Extensions.AI`。具體的 client 類別刻意設為 `internal`，所以沒有任何 RimLLM 的 client 型別需要你對接。

---

## 📖 功能特色

1. **多供應商支援**
   * 原生支援 Google **Gemini**、**OpenAI**、**DeepSeek**、**Groq**、**Grok (xAI)**、**Z.ai**、**OpenRouter**、**Kimi**、**MiniMax**、**Qwen** 與 **NVIDIA**。
   * 支援 **OpenAI 相容 API**，可設定任何本地或第三方相容端點（LM Studio、Ollama、LocalAI、vLLM 等）。預設端點為 `http://localhost:1234/v1`，並支援 API 金鑰。
   * **Kimi**、**MiniMax**、**Qwen** 提供一鍵切換「使用中國專用端點」（預設關閉），以改善連線品質。
2. **容錯與模型 Fallback**
   * **客戶端 Fallback 鏈**：可設定由主要模型與多個精確備援模型組成的鏈。目前模型遇到逾時、速率限制（HTTP 429）或連線錯誤時，框架會無縫往下切換。UI 產生的項目為 `Provider:Model` 形式；框架仍相容只填供應商的舊項目，並使用該供應商的預設模型。
   * **OpenRouter 服務端 Fallback**：OpenRouter 的項目可以用逗號列出多個模型 —— 把 `ChatOptions.ModelId` 設為 `"OpenRouter:model-a, model-b, model-c"`（Fallback 鏈項目也接受同樣的格式）。此時供應商會改送 OpenRouter 的 `models` 陣列而非單一 `model` 欄位，把「要用哪一個」的決定交給 OpenRouter 服務端；只填一個模型名時仍送出一般的 `model`。由 `TestOpenRouterFallbackPayload` 驗證。注意設定介面是從快取模型清單一次挑一個模型來組出項目，因此這種多模型寫法來自呼叫端程式碼，而不是 Fallback 鏈編輯器。
   * `Retry-After` 在所有路徑上都支援 RFC 7231 允許的兩種格式 —— 延遲秒數與 HTTP 日期。
   * **重試間的指數退避**：等待時間每次翻倍（`RetryDelay × 2ⁿ`）並加上 ±20% 抖動，上限 60 秒。遇到限流還用固定間隔連打，只會再一次撞上同一面牆，把重試額度白白耗光；伺服器透過 `Retry-After` 要求更長的等待時仍以其為準。
   * **冷卻以「供應商 + 模型」為單位。**健康帳本以 `Provider:Model` 為鍵。備用鏈上常同時掛著同一個供應商的多個模型（例如三個 OpenRouter 模型），只以供應商為鍵會讓其中一個模型限流就把另外兩個健康的模型一起連坐。
   * **一次請求只記一次失敗。**同一次請求的所有重試合計只計一次失敗。逐次記錄的話，單一次網路抖動（預設設定下共 4 次嘗試）就能把健康的目標推過熔斷門檻，冤枉凍結數分鐘。
   * **路由策略**：`PriorityFailover`（依鏈順序）、`MinLatency`、`RoundRobin` 與 `LowestCost`。`LowestCost` 直接沿用框架已經依 API 費率自動判定的模型分級排序，不需要另外維護一份價格表。所有排序都是穩定的，同級的候選會保留鏈本身的順序。
3. **AES-256 設定加密**
   * API 金鑰以 AES-256 對稱加密儲存，降低設定檔中出現明文金鑰的風險。這是混淆等級的保護 —— 詳見下方[安全性說明](#-安全性說明)。
   * 設定介面預設也會**遮罩金鑰**（只留頭尾，仍可辨認自己設定了哪一把），每一列另有切換鈕可暫時顯示以便編輯。這防的是與加密不同的外洩途徑：截圖、回報問題與直播。
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
   * 原生支援 **Gemini 3.7 Flash / 3.1 Pro (Thinking)**、**OpenAI GPT-5.6 Sol / GPT-5.5**、**DeepSeek-V4-Pro / Flash**、**Grok 4.6**、**Qwen3.8-Max**、**Kimi K3**、**GLM-5.3-Flash** 等現代深度推理與思考模型。
   * 框架會把 API 回傳的思維鏈（OpenAI 協定的 `reasoning_content`、Gemini 的 `thought` 欄位）正規化成 MEAI 原生的 `TextReasoningContent`，放在 `ChatResponse.Messages` 與 `ChatResponseUpdate.Contents` 裡交給你。它刻意**不**被揉進 `ChatResponse.Text`：否則每一個讀 `Text` 的呼叫端（結構化輸出、快取鍵、JSON 解析）都得先把標籤剥掉。要保留或丟棄，依內容型別過濾即可。
   * GUI 對話測試頁自己從這些內容組出 `<think>...</think>` 標籤，再將思維鏈以灰色斜體呈現。那個扁平化是呈現，不是協定。
   * **推理強度控制**：預設為「自動」，讓各供應商執行自己的自適應或動態思考設定（Gemini 的 `thinkingBudget = -1`、OpenAI 的動態 `reasoning_effort` 等）。也可以完全關閉推理，或手動設為低／中／高。
   * **強度對所有供應商、所有模型都有效**。線上格式由各供應商自行宣告，框架不再靠模型名猜測：頂層 `reasoning_effort`（OpenAI、xAI、Groq、MiniMax、NVIDIA、OpenAI 相容端點）、OpenRouter 的統一 `reasoning` 物件、`thinking: {type}` 加強度（DeepSeek、Z.ai、Kimi）、`enable_thinking` 搭配 `thinking_budget`（Qwen），以及 Gemini 的 `thinkingConfig`。詞彙差異逐家對應 —— Kimi 只吃 low/high/max，xAI 的推理無法關閉，關閉請求在該家會被忽略而不是換來 400。
   * **未知模型先樂觀送出，再從服務端學習**。以模型名列白名單必然腐化：框架先前只對 `o1`/`o3` 開頭的模型送出強度，其餘一律靜默丟棄。現在除了少數已知不具思考能力的系列之外一律送出；若服務端以 400 拒絕該參數，框架會記下這組 (供應商, 模型)、去掉參數重打一次，並在本次遊戲執行期間不再送。漏掉一個模型的代價因此是一次重試，而不是永久失效。同一套機制也涵蓋 `temperature` —— GPT-5 等推理模型會直接拒絕它。記憶只存在於本次執行，模型日後支援了，重開遊戲就會重新嘗試。
   * **Markdown 呈現**：對話測試頁會把模型回覆轉成 Unity 舊版 rich text，標題、粗體、斜體、清單、引用、連結與程式碼區塊會以結構呈現，而不是印出 `**`、`` ` `` 這些原始符號。舊版 IMGUI 只認得 `b`、`i`、`size`、`color`、`material`、`quad` 六個標籤，沒有對應標籤的結構（縮排、表格）以空白與符號近似。底線斜體刻意不支援，因為會與 `snake_case` 識別字衝突。
9. **上下文快取與 Prompt 快取**
   * 原生支援 **Gemini context caching** 與 **OpenAI prompt caching**。在 `RimLLMChatOptions` 設定 `CachedContext`，框架會提交 `SystemPrompt + CachedContext` 進行快取，大幅降低高頻重複請求的輸入 Token 成本與延遲。
   * **成本防呆**：Gemini 顯式快取有最小尺寸門檻，內容過小時框架會跳過快取改用 `systemInstruction`，避免建立費永遠回收不了。同一份上下文的快取建立也以鎖序列化，防止產生重複資源。
   * **量化節省**：用量統計會解析 API 回傳的快取命中 Token（OpenAI `cached_tokens`、Gemini `cachedContentTokenCount`）並套用折扣費率估算成本，讓成本面板反映真實節省。
   * **本地回應快取**（預設關閉，且與上面兩項不同 —— 那兩項是「供應商端」的快取，這一項完全不離開玩家的電腦）。啟用後，逐字相同的請求會直接回傳先前的結果，完全不發出 API 呼叫：零成本、零延遲，也不會產生任何 Token 用量記錄。快取鍵涵蓋所有會影響輸出的欄位 —— 每一則訊息（角色、文字，以及工具結果之類的非文字內容）、目標模型、最低相容等級、快取上下文、temperature、最大輸出 Token、思考強度、是否關閉思考、結構化輸出型別，以及所有會原樣送達供應商的取樣參數（`TopP`、`TopK`、`FrequencyPenalty`、`PresencePenalty`、`Seed`、`StopSequences`）—— 但刻意不含 `modId` 與 `Priority`，它們只影響節流與排隊順序。比對是精確比對，不做語意相似度。代價是相同輸入必然得到相同輸出，這對敘事性文本未必是玩家要的，因此預設關閉，並提供玩家自訂的存活時間（1–120 分鐘，寫入當下就固定）與 256 筆上限。過期與容量淘汰交給 `Microsoft.Extensions.Caching.Memory.MemoryCache`（版本釘 `10.0.11`，以對齊 MEAI 已經帶進來的 `Caching.Abstractions` 組件識別），框架只負責判定「什麼算同一個請求」。只存在記憶體中，不寫入存檔。
10. **Embedding SDK**
    * 框架公開由 Google、Ollama 或 OpenAI 相容端點支援的 embedding 功能。其他 Mod 可透過 `RimLLMProvider.CreateEmbeddingGenerator` 取得標準 `IEmbeddingGenerator`，用於語意檢索與分群。
    * 三種線上來源全走官方 SDK：Google 使用 `Google.GenAI` 的 `EmbedContentAsync`；Ollama 與自架服務使用 OpenAI SDK 的 `EmbeddingClient`（Ollama 走其 OpenAI 相容的 `/v1` 端點）。因此「Embedding 端點」欄位填的是**服務根位址**（如 `http://localhost:11434/v1`）；填入完整 `/embeddings` 路徑會自動正規化。
    * 設定頁可直接抓取可用模型清單，不必憑記憶輸入名稱。Google 依模型自己宣告的 `supportedActions` 是否包含 `embedContent` 精確篩選，只列出真正的 embedding 模型。OpenAI 相容端點的 `/v1/models` 不回傳能力資訊，因此該清單只**排序**（把像 embedding 的名稱排前面）而不過濾 —— 本地伺服器的模型名由使用者自訂，過濾會把合法選項藏起來。沒有 `/v1/models` 的伺服器仍可手動輸入。
    * Embedding 屬計費 API，因此與一般生成請求共用同一套防濫用檢查；其金鑰採用與供應商金鑰相同的 AES 加密。
11. **原生 Tool Calling（函式呼叫）**
    * 完整支援 Microsoft.Extensions.AI Tool Calling 標準（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`、`FunctionResultContent`）。
    * 針對 OpenAI 與 Google Gemini 模型提供雙向工具 Schema 與訊息協定轉譯。
    * 提供 `RimWorldFunctionInvoker.AsMainThreadFunctionInvokingClient()`，自動將工具叫用委派排入 Unity 主執行緒執行，杜絕 RimWorld 跨執行緒崩潰風險。
    * 當請求中包含工具時，自動繞過本地回應快取以確保狀態副作用一致性。

---

## 🛠️ 架構設計

### 1. 統一介面與調度核心（`IChatClient` / `IEmbeddingGenerator` 與 `RimLLMProvider`）

* 框架對外暴露標準的 Microsoft.Extensions.AI 介面。呼叫端只面對 `IChatClient` 或 `IEmbeddingGenerator`，完全不需要知道實際由哪個供應商或模型處理 —— 調度與 Fallback 輪替由 `RimLLMManager` 負責。
* `CreateChatClient` 回傳的是一疊 MEAI `DelegatingChatClient` 中介層——思考強度正規化、回應快取、防濫用節流、預算保護、優先權佇列——最內層是沿 Fallback 鏈路由的 `FailoverChatClient`。每一層都是 `internal`。使用端會碰到的框架專屬型別只有 `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException` 與 `LLMError`，其餘跨越邊界的全是 MEAI 型別。
* `modId` 是純標籤，不是憑證。它是每個 Mod 防濫用節流與遙測歸屬的鍵，不需要任何註冊呼叫。

### 2. Unity 主執行緒派送器（`RimLLMDispatcher`）

* 網路請求在背景執行緒池上非同步執行，但多數 Unity API 與 RimWorld 邏輯並非執行緒安全 —— 從背景執行緒呼叫會造成崩潰或 TPS 掉幀。
* `RimLLMDispatcher` 是 MonoBehaviour 單例，以 `ConcurrentQueue` 收集背景執行緒的回呼，並在 Unity 每幀的 `Update` 中派送回主執行緒。

### 3. 串流橋接（`Channel<T>`）

* Executor 的串流 API 是回呼形式（`Action<ChatResponseUpdate> onUpdateReceived`），而 MEAI 要的是 `IAsyncEnumerable<ChatResponseUpdate>`。兩者之間以無界的 `System.Threading.Channels.Channel<T>` 橋接，消費端就是 `ChannelReader.ReadAllAsync()`。
* update 以**原樣**穿過這座橋——你列舉到的就是供應商產生的那個物件，只有 `ModelId` 被改寫。框架不再自行合成一個收尾 update，因此 `UsageContent`、`FinishReason` 與 `ResponseId` 只在供應商真的送出時才存在。
* 由於 `IAsyncEnumerable` 是透過 `bclasync` extern alias 進入本專案，C# 8 無法對它編譯 async iterator。`ReadAllAsync()` 直接繞過這個限制：它回傳的正是同一顆組件的 `IAsyncEnumerable`，因此不必手寫任何 iterator。
* 一層薄包裝會解開 `ChannelClosedException`，讓生產端的失敗以原始的 `RimLLMException` 呈現給呼叫端。

### 4. 統一的 HTTP 錯誤對照（`LLMErrorMapper`）

* HTTP 狀態碼轉換為 `LLMError` 的規則集中在 `LLMErrorMapper` 一處，由官方 SDK 路徑（`ClientResultException`）與 embedding 服務共用。
* `Retry-After` 的解析也在其中，基於 `RetryConditionHeaderValue`，因此延遲秒數與 HTTP 日期兩種格式在各處行為一致。
* 這使得「哪些狀態碼可重試」與「哪些代表 Schema 遭拒應降級」在各處行為完全相同。第三方自訂供應商也能引用同一份對照。

### 5. 容錯的結構化輸出（structured output 與 JSON repair）

* 開發者經常需要模型回傳特定的 JSON 結構。
* 內建的 OpenAI 與 Gemini 供應商優先使用官方 SDK 的原生結構化輸出：OpenAI 透過 `IChatClient` 的 JSON Schema response format，Gemini 透過 `ResponseMimeType = "application/json"` 加 `ResponseSchema`。框架會先驗證必要成員與 null 狀態，再反序列化為目標 C# 物件。
* schema 本身會依供應商方言產生 —— 所有成員都列入 `required`，選填性以 `["integer","null"]` 聯集（OpenAI）或 `nullable: true`（Gemini）表達。詳見[架構設計 §6](#6-官方-sdk-與供應商職責)。
* `RepairJson` 回退機制僅在供應商不支援原生 Schema、服務拒絕 Schema，或模型仍回傳格式錯誤內容時啟用。它處理 Markdown 圍籬（如 ` ```json `）、未閉合括號、尾隨逗號與 JSON 區塊擷取。

### 6. 官方 SDK 與供應商職責

* 主專案與測試專案維持 `net472`；RimWorld Mod 不需要遷移到 .NET 8。官方 SDK 的相依 DLL 隨 Mod 發佈，並由 `ProviderSdkIntegrationTests` 逐一載入並反射，讓遺漏的間接相依組件在建置階段就失敗而不是在遊戲裡。要注意這項檢查跑在真正的 .NET Framework 上，因此拓不到「在這裡存在、但 RimWorld 的 Mono BCL 沒有」的型別 ——下方的 `DataAnnotations` 就是這種失敗，只能靠實際啟動遊戲才抓得到。雖然 .NET Framework 將 `System.ValueTuple` 視為框架組件，建置仍明確部署其 `4.0.5.0` DLL，以避免 RimWorld 的 Mono 反射 MEAI 時發生 `ReflectionTypeLoadException`。
* **OpenAI** 使用 `OpenAI` SDK `2.13.0` 搭配 `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` `10.9.0`。針對 OpenAI SDK 2.13.0 與 `System.ClientModel` 1.15.0 在實驗性 `ChatCompletionOptions.Patch` API 內部因 `PropagateSet` 缺乏 null 防護而擲出 `NullReferenceException` 的問題，框架透過 `OpenAIPatchExtensions.DisablePatchPropagators()` 清除傳播委派，安全恢復底層 JSON Patch 寫入機制以注入 `reasoning_effort`、`response_format`、`max_tokens` 與 `models` 欄位。內建的 `OpenAIProvider` 透過 `ChatClient.AsIChatClient()` 進入共用 manager。只有真正實作 OpenAI Chat Completions 協定的端點（LM Studio、Ollama、vLLM…）才適合 OpenAI 相容轉接。
* **Gemini** 使用官方 `Google.GenAI` `1.21.0`，以 API 金鑰建立 Gemini Developer API 用戶端。文字、串流、原生 Schema、思考、上下文快取與安全設定全走原生 `Google.GenAI` 路徑（在程式碼中以測試縫隔離：`CreateGenAiClient`、`GenerateContentNativeAsync`、`GenerateContentStreamNativeAsync`、`CreateCachedContentNativeAsync`）。Gemini 絕不以 `OpenAI.Chat.ChatClient` 模擬。
* **每個內建供應商都走官方 SDK**：OpenAI 家族（OpenAI、OpenRouter、DeepSeek、Groq、Grok、Z.ai、Kimi、MiniMax、Qwen、NVIDIA、OpenAICompatible）使用 `OpenAI` SDK `2.13.0` 加 MEAI 的 `IChatClient`；Gemini 走原生 `Google.GenAI` 路徑。模型清單使用 `OpenAIModelClient.GetModelsAsync()`，而非自行拼 `/models` URL 再解析 JSON。
* **框架已無任何 raw HTTP 路徑。** 建立 Gemini `cachedContents` 顯式快取是最後一處，現已改走 `Client.Caches.CreateAsync`，回傳型別化的 `CachedContent`（`ExpireTime` 直接是 `DateTime?`，不需要再解析字串）。本文件先前宣稱 `Caches` 只暴露 `ListAsync` —— 那是錯的，而且從未被驗證過；對實際組件反射顯示 `CreateAsync`、`GetAsync`、`UpdateAsync`、`DeleteAsync`、`ListAsync` 全是公開成員。移除該路徑後，整個 HTTP 傳輸層與認證 Header 處理都一併刪除。
* **JSON Schema 產生走 `System.Text.Json` 的 `JsonSchemaExporter` 加一層正規化**（`RimLLMSchemaBuilder`），分三階段。**Stage A** 由 exporter 匯出完整 JSON Schema。**Stage B** 正規化成所有供應商都接受的受限子集：解析並展開 `$ref` 指標、截斷循環與過深巢狀、把可為 null 的聯集收斂成單一 `type`、只保留關鍵字白名單。**Stage C** 套用目標供應商的方言。方言有兩種，取自 `LLMProviderCapabilities.PreferredSchemaProfile`，第三方供應商因此能宣告自己的方言：OpenAI 把選填成員寫成 `["integer","null"]` 聯集，Gemini 則寫成單一 `type` 加 `nullable: true`。
  * **直接呼叫 exporter，不經過 MEAI 的 `AIJsonUtilities.CreateJsonSchema` 包裝層。** 該包裝層出貨的是 `net462` 資產，會參考 `System.ComponentModel.DataAnnotations`（用來讀 `[EmailAddress]`、`[Range]` 之類的驗證屬性豐富 schema）。RimWorld 的 Mono BCL 沒有那個組件，所以實機上會拋 `TypeLoadException: Could not resolve type … 'EmailAddressAttribute' in assembly 'System.ComponentModel.DataAnnotations, Version=4.0.0.0'`，整份 schema 產生靜默降級成舊的反射實作 —— 而單元測試跑在有 GAC 的真 .NET Framework 上，完全看不出來。`System.Text.Json` 沒有該參考，而且它就是 MEAI 內部使用的同一個引擎，直呼不損失任何能力。MEAI 唯一多做而仍需要的 `[Description]`，改由 Stage B 自行讀取。這條限制由 `SchemaGenerationEngineHasNoDataAnnotationsDependency` 釘住。
  * 直呼 exporter 有兩個後果：列舉只會輸出 `{"enum":[…]}` 而不帶 `type`（Stage B 由列舉值反推型別，否則所有列舉成員都會消失），而且它完全沒有 `description` 的概念（Stage B 自行讀取成員與類別上的 `[Description]`）。
  * **循環在 CLR 型別層截斷，而非 JSON pointer 層。** exporter 會把遞迴成員先完整展開一輪、其中才出現指回祖先的 `$ref`，只靠 pointer 偵測就會多送一整層 —— 實測遞迴測試型別從 789 字元漲到 3119 字元，而那是每次請求都要付的 prompt token。由 `RecursiveSchemaStaysCompact` 守住。
  * **巢狀深度上限依方言而異。** OpenAI 的 strict structured output 最多允許 5 層巢狀（另有全域 100 個 property 的上限），超過會被服務端拒絕並靜默降級成提示式 JSON，因此 OpenAI 方言在 5 層截斷；Gemini 沒有這條限制，維持框架整體的 8 層。注意 100 個 property 的上限目前**尚未**強制。
  * exporter 的原始輸出不能直接送。`Google.GenAI.Types.Schema.Type` 是單一列舉值，聯集型別會讓 `Schema.FromJson` **靜默回傳 null** —— Gemini 於是完全收不到 schema，而且沒有任何錯誤浮上來。這一點由一對迴歸測試釘住（`RawMeaiSchemaIsRejectedByGoogleSchemaFromJson` 與 `GeminiProfileSchemaIsAcceptedByGoogleSchemaFromJson`），不再只是本文件裡的一句宣稱。
  * `$ref` **不只**用於遞迴 —— MEAI 也用它來為重複出現的型別去重，所以一律截斷 `$ref` 會靜默刪掉正常成員。正規化層會解析 JSON pointer，只有在它指向目前展開路徑上的祖先時才視為循環。
  * **所有成員一律列入 `required`**，選填性改由型別表達。OpenAI 的 strict structured output 要求 `required` 涵蓋每一個 property，所以舊行為（`Nullable<T>` 不列入 `required` 卻仍送 `strict: true`）在服務端會被拒絕，並被靜默降級成提示式 JSON。
  * 由於 schema 由 System.Text.Json 的 exporter 產生、反序列化卻是 Newtonsoft，兩者的成員契約由一個 contract modifier 對齊（納入欄位、尊重 `[JsonIgnore]`、套用 `[JsonProperty]` 名稱、排除唯讀成員），並有測試斷言兩邊成員集合一致。**結構化輸出的型別請勿使用自訂的 Newtonsoft `JsonConverter`** —— 它會改變 wire 形狀，而 exporter 看不到。
  * 若 `JsonSchemaExporter` 在 RimWorld 的 Mono 環境不可用，產生器會記錄警告、永久降級回舊的反射實作，並強制關閉 `strict`。
* 供應商專屬 SDK 絕不出現在 `RimLLMManager` 或公開 SDK facade 中；共用層只相依 `IChatClient`、`LLMProviderCapabilities` 與既有的 `ILLMProvider` API。API 金鑰一律來自加密設定，絕不寫入原始碼或一般日誌。

---

## 🔐 安全性說明

為避免誤解，以下誠實說明每項安全機制實際防護的範圍：

* **API 金鑰加密屬混淆等級保護。** 金鑰在設定檔中以 AES-256 加密，加密金鑰由固定種子與裝置識別碼（`deviceUniqueIdentifier`）衍生。這能防止設定檔被複製到其他機器後被讀出明文，也避免同步或分享設定時意外外洩 —— 但**無法**防禦在同一台機器上執行的程式碼（包含其他 Mod），因為加密邏輯與素材都在同一個行程內，有心人可還原明文。請把它視為防呆與防止意外揭露，而不是保險箱。
* **刻意不做呼叫者驗證。** 舊版會把每個 `modId` 綁定到一個呼叫端組件。這個檢查擋不住惡意 Mod —— 全部都在同一行程內，反射就能繞過 —— 而且是先到先贏，載入較早的 Mod 可以占用某個 id，讓正牌擁有者在啟動時直接擲出例外。因此移除：它把可忽略的偽造風險換成了真實的阻斷服務風險。
* **金鑰不會進入 URL 或日誌。** 所有供應商都以 HTTP Header 傳遞金鑰；日誌輸出一律經過 `SanitizeForLog` 並截斷長度，診斷匯出中的裝置識別碼也會遮罩。

---

## 📜 授權條款

本模組原始碼以 **MIT License** 釋出 —— Copyright (c) 2026 **mushroomTW**。詳見 [LICENSE](LICENSE)。

隨附於 `Assemblies/` 的相依組件維持各自的授權：Microsoft.Extensions.AI、OpenAI .NET SDK 與 Newtonsoft.Json 為 MIT；Google.GenAI 與 Google.Apis.\* 為 Apache-2.0。RimWorld 本身的組件屬於 Ludeon Studios，本模組不予散布。

---

## 🧪 單元測試與驗證

專案在 `Source/RimLLM Framework.Tests`（與主專案並列的獨立專案）附有完整的單元測試套件，涵蓋 AES 加解密、模型 Fallback、JSON Schema 產生（正規化、各供應商方言，以及與 `Google.GenAI` `Schema.FromJson` 的成對對照測試）與修復、HTTP 錯誤對照、`Retry-After` 解析、`ChatOptions` 複製、串流重試與預算控制。

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

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
  <PackageReference Include="Microsoft.Extensions.AI" Version="10.10.0" ExcludeAssets="runtime" />
</ItemGroup>
```

* [`Microsoft.Extensions.AI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0) —— 使用端 Mod 只需要這一個。它會帶進 `Microsoft.Extensions.AI.Abstractions`，`IChatClient` 就在裡面。
* [`Microsoft.Extensions.AI.OpenAI` 10.10.0](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/10.10.0) —— 框架另外會一併發佈這一顆。只有在你要自己建構 OpenAI SDK 用戶端時才需要參考；單純呼叫 `RimLLMProvider.CreateChatClient` 的 Mod 不需要。

> [!IMPORTANT]
> **版本必須釘死在 `10.10.0`。** 組件識別必須與框架載入的那一份完全一致。使用端 Mod 也不要把 `CopyLocalLockFileAssemblies` 設成 `true` —— 那正是造成上述 DLL 重複問題的原因。
>
> **從舊版框架升上來要注意：**MEAI 的組件版本是跟著 `major.minor` 走的，`10.8.3` 產生的是 `10.8.0.0`，`10.10.0` 產生的是 `10.10.0.0`。因此對著舊版 MEAI 編譯的使用端 Mod 必須把這行的版本改掉並重新編譯 —— 這不是原始碼層的破壞性變更，但舊的二進位已經對不上框架載入的那一份。

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

框架本身的第三方整合層需要 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)（`brrainz.harmony`），
已在 `About.xml` 宣告為必要相依。`0Harmony.dll` **不會**隨 `Assemblies/` 出貨，執行期由 Harmony Mod 提供。

---

## 💻 SDK 使用方式

**如果你已經會用 [`Microsoft.Extensions.AI`](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.10.0)，你就已經會用這套 API。**

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

訊息清單與 `ChatOptions` 的用法與 MEAI 文件完全相同。唯一與本框架有關的規則是：不設定 `ModelId` 時，實際由哪個供應商與模型執行，交給玩家設定的 Fallback 鏈決定；要指定就填 `"供應商:模型"` 形式的項目。指定的模型不論路由策略為何、是否已在鏈上，一律最先嘗試，失敗後才輪到鏈上其餘候選。`RimLLMProvider.GetFallbackChain()` 會依設定順序回傳玩家備援鏈的副本，可用來做模型選單。

有兩個欄位沒設定時框架會補預設值：`MaxOutputTokens` 為 **1024**、`Temperature` 為 **0.7**。批次翻譯、多角色對話、欄位很多的結構化輸出這類長回應請自行設定 `MaxOutputTokens`，否則回覆會在 1024 個 token 處被截斷。

回傳的 `ChatResponse` 就是供應商產生的那一個，除了 `ModelId` 被改寫成 `"供應商:模型"`（讓你在 failover 之後仍分辨得出實際是誰回的）之外，一律原樣交還。`ResponseId`、`CreatedAt`、`ConversationId`、`Usage`、`FinishReason`、`RawRepresentation` 與 `AdditionalProperties` 都是供應商設什麼就是什麼，包括 `null`。`Usage` 為 `null` 代表供應商沒有回報 token 數，不是這次呼叫免費。

### 串流

串流是標準 MEAI 的 `GetStreamingResponseAsync` / `await foreach`，語法見 MEAI 文件。框架額外保證一件事：整條 Fallback 鏈失敗時，原始的 `RimLLMException` 會從 `await foreach` 重新擲出，串流不會無聲結束。

框架**不會**把 update 派送到 Unity 主執行緒。迴圈在哪個執行緒續行，取決於你自己的 `await`：在主執行緒上開始 `await foreach`，Unity 的 `SynchronizationContext` 會讓每一輪都回到主執行緒；若從 `Task.Run` 或 `ConfigureAwait(false)` 之後開始，迴圈本體就跑在執行緒池上，此時只能透過 `RimLLMDispatcher.EnqueueOnMainThread` 碰遊戲或 UI 狀態。

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
> 請用這個而不是 MEAI 自己的 `GetResponseAsync<T>`。MEAI 的原始 schema 形狀（union 型別、`$ref`、無上限巢狀）會被多家供應商的 strict structured output 拒絕，而且 MEAI 沒有 JSON 修復路徑。RimLLM 驅動的是同一個底層 `JsonSchemaExporter`，但在其上加了正規化層與單一 OpenAI 相容方言。詳見[架構設計 §6](#6-官方-sdk-與供應商職責)。

### 原生 Tool Calling（函式呼叫）

RimLLM Framework 原生支援 Microsoft.Extensions.AI 的 Tool Calling（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`）。所有內建供應商都走 OpenAI 協定，工具定義與呼叫只有一種 wire 形狀。`AIFunctionFactory.Create` 在遊戲內可正常使用：框架出貨的是 `Microsoft.Extensions.AI.Abstractions` 的 `netstandard2.0` 版本，讓 MEAI 的 schema 產生不再碰 RimWorld Mono 沒有的 `System.ComponentModel.DataAnnotations` 組件（詳見[架構設計 §6](#6-官方-sdk-與供應商職責)）。

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

#### 3. Agent 作者須知

當一個請求變成多輪工具迴圈時，下列幾點會影響你的設計：

* **防濫用節流以外層請求計數，不以迴圈輪次計數。** 每個 Mod 的節流（預設 10 秒內 10 次，超過冷卻 60 秒）看得到 `FunctionInvokingChatClient` 發出的每一次呼叫，但最後一則訊息是 `ChatRole.Tool` 工具結果的呼叫會被視為「啟動這個迴圈的那個請求」的延續，不計入時間視窗。已在冷卻中的 Mod 仍然會被擋，續輪也不例外。判定靠訊息形狀，因此直接用 MEAI 自己的 `FunctionInvokingChatClient` 也適用。
* **送出前先確認工具到得了模型。** 不支援原生函式呼叫的供應商會在呼叫前把請求的 `Tools` 移除。`RimLLMProvider.GetEffectiveCapabilities()` 回傳目前備援鏈可能路由到的所有候選能力的**交集** —— 那裡的 `SupportsFunctionCalling` 為 `true`，就沒有任何候選會丟掉你的工具。傳入與 `ChatOptions.ModelId` 相同格式的 `"Provider:Model"` 字串可以縮小範圍。
* **工具仍被丟掉時，回應會告訴你。** `response.WereToolsStripped()`（串流的每個 `ChatResponseUpdate` 也有同名方法）為 `true` 代表實際回答的供應商根本沒看過工具定義。這和「模型決定不呼叫工具」是兩回事，要分開處理。
* **fallback 可能在迴圈中途換供應商。** 每一輪都是獨立請求，第 3 輪可能由第 1 輪之外的供應商回答，歷史裡帶著前一家產生的 `tool_call_id`。所有內建供應商都走 OpenAI 協議，因此可以互通；若你的邏輯依賴實際回答的模型，檢查 `response.ModelId`（`"Provider:Model"`）。
* **框架不保存任何對話狀態。** 歷史、上下文修剪與 token 預算都由呼叫端負責；框架只負責把溢位對應成 `LLMError.ContextWindowExceeded`。

### Embedding 向量

形狀一樣：一行框架呼叫，之後全是標準 MEAI：

```csharp
IEmbeddingGenerator<string, Embedding<float>> generator =
    RimLLMProvider.CreateEmbeddingGenerator("myai.mod");
```

`GenerateAsync`、`GeneratedEmbeddings<T>` 與 `Embedding<float>` 的行為與 MEAI 文件相同。每個 `Embedding<float>` 都帶著實際算出它的 `ModelId`，`GeneratedEmbeddings.Usage` 則在供應商有回報時帶回輸入 token 數—— OpenAI 相容端點會回報，Gemini 公開 API 不會（它的 `tokenCount` 限 Enterprise 平台），因此在 Gemini 下 `Usage` 維持 `null`。與對話一樣，`null` 代表供應商沒有回報，不是這次呼叫免費。Embedding 供應商預設為**停用**；玩家選擇之前，`GenerateAsync` 會擲出 `RimLLMException`。

同一次 `GenerateAsync` 的多筆輸入會合成一次批次請求送出（為了上限較小的端點，每 100 筆分一批），不再每筆一次 HTTP 來回；供應商回報的輸入 token 也和對話請求一樣計入用量看板與每日預算。

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

`CachedContext` 不為空時，`EnableContextCaching` 會自動開啟。可重用的前綴會併入系統訊息，具備服務端 prompt caching 的供應商（OpenAI，以及經 OpenAI 相容端點存取的 Gemini）會自動對重複前綴打折。

### 你不必自己寫的部分

這才是這個框架存在的意義。以下全部已經在那一個 `IChatClient` 後面完成：

| 你可以省略 | 因為框架已經做了 |
| --- | --- |
| API 金鑰的儲存與 UI | 使用 OS 每使用者保護金鑰的 AES-256 加密設定，所有 Mod 共用同一份 |
| 挑選供應商或模型 | 玩家設定的 Fallback 鏈，項目為 `Provider:Model` 形式 |
| 重試與 `Retry-After` | 逾時／429／連線錯誤自動重試，兩種標頭格式都支援 |
| 供應商之間的容錯切換 | 回應開始輸出之前，自動沿 Fallback 鏈降級 |
| 處理掛掉的供應商 | 熔斷器，連續失敗後以指數退避冷卻 |
| 跨 Mod 的流量控制 | 全域優先佇列與並行上限，避免多個 Mod 同時打 API 造成掉幀 |
| 費用控管 | 每日預算，可選硬性阻擋／模擬回應／改用免費模型／詢問玩家 |
| 用量與費用回報 | Debug 分頁的各供應商 Token 與成本看板 |
| 推理模型的差異 | `reasoning_content` 統一正規化為 MEAI 的 `TextReasoningContent` |
| 格式錯誤的 JSON | 修復 Markdown 圍籬、未閉合括號與尾隨逗號，再抽出 JSON 區塊做第二次解析 |
| 主執行緒切換 | 記憶體內的請求日誌更新派送回 Unity 主執行緒；公開 `RimLLMDispatcher` 供你派送自己的回呼 |
| 原生 Tool Calling 與主執行緒排程 | 所有供應商經 OpenAI 協定送出工具定義；工具委派自動排入 Unity 主執行緒 |

### API 表面速查

`using RimLLM_Framework;` 會引入 14 個公開型別。多數 Mod 只會碰到第一列：

| 分層 | 型別 | 什麼時候需要 |
| --- | --- | --- |
| **呼叫模型** | `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException`、`LLMError`、`RimLLMClientExtensions`（`GetResponseObjectAsync<T>`、`WereToolsStripped`）、`RimWorldFunctionInvoker` | 一定會用到 —— 這就是全部的使用端 API |
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
   * **HTTP 402 不重試**：402 代表帳戶餘額已經用完，餘額不會在退避的幾十秒內變出來，因此直接換下一個候選，不先耗光重試額度。訊息只是提到「quota」的 429（Gemini 每分鐘限流）仍照常退避重試。
   * **退避期間不占併發名額**：全域 `MaxConcurrentRequests` 的名額以每一次嘗試為單位、只在真正打 API 時持有，等待指數退避（最長 60 秒）的請求不會讓其他 Mod 的請求排在後面。
   * **重試間的指數退避**：等待時間每次翻倍（`RetryDelay × 2ⁿ`）並加上 ±20% 抖動，上限 60 秒。遇到限流還用固定間隔連打，只會再一次撞上同一面牆，把重試額度白白耗光；伺服器透過 `Retry-After` 要求更長的等待時仍以其為準。
   * **冷卻以「供應商 + 模型」為單位。**健康帳本以 `Provider:Model` 為鍵。備用鏈上常同時掛著同一個供應商的多個模型（例如三個 OpenRouter 模型），只以供應商為鍵會讓其中一個模型限流就把另外兩個健康的模型一起連坐。
   * **一次請求只記一次失敗。**同一次請求的所有重試合計只計一次失敗。逐次記錄的話，單一次網路抖動（預設設定下共 4 次嘗試）就能把健康的目標推過熔斷門檻，冤枉凍結數分鐘。
   * **路由策略**：`PriorityFailover`（依鏈順序）、`MinLatency`、`RoundRobin` 與 `LowestCost`。`LowestCost` 直接沿用框架已經依 API 費率自動判定的模型分級排序，不需要另外維護一份價格表。所有排序都是穩定的，同級的候選會保留鏈本身的順序。透過 `ChatOptions.ModelId` 指定的模型不參與排序，固定排在第一位。
3. **AES-256 設定加密**
   * API 金鑰以 AES-256 對稱加密儲存，金鑰由目前 OS 使用者的保護機制包裝。新資料使用 `v3:` 格式；`v1`／`v2` 的裝置衍生密文僅供遷移，下一次存檔時會改寫成受保護金鑰格式。
   * 設定介面預設也會**遮罩金鑰**（只留頭尾，仍可辨認自己設定了哪一把），每一列另有切換鈕可暫時顯示以便編輯。這防的是與加密不同的外洩途徑：截圖、回報問題與直播。
   * 所有供應商（含 Gemini）都以 HTTP Header 傳遞金鑰，絕不放在請求 URL，避免金鑰進入代理或伺服器的存取日誌。
   * RimWorld 的所有 Mod 都在同一個遊戲行程內執行。本框架不宣稱能阻止惡意 Mod 讀取記憶體、對公開 API 使用反射，或以其他行程內手段繞過邊界。
4. **精緻的可捲動多欄 GUI**
   * 直覺的模型 chip 流式格線與點擊選單，支援一鍵加入 Fallback 容災鏈或複製模型名稱，完整名稱以 tooltip 顯示。
   * 供應商選單具備當前項目聚焦側條，並即時以顏色標示「已啟用」、「未啟用」與「未配置金鑰」狀態。
   * 對話測試頁升級為現代化卡片式對話氣泡，清楚分開使用者發言與 AI 回覆，支援基於 Markdig AST 的 Markdown 渲染、氣泡底部單則回覆一鍵複製，並能在生成中隨時一鍵中斷停止。每則 AI 氣泡底部另有微型標籤顯示實際應答的模型（點擊可複製）、耗時與 Token 用量 —— 供應商有回報用量時為精確值，否則以 `~` 標示為估算值 —— 並隨對話歷史一併保存。
   * Embedding 設定改採與供應商頁相同的三欄式版面：每個 Embedding 供應商（Google Gemini、OpenAI、Ollama、OpenAI 相容）各自一個子分頁，擁有獨立的模型、端點與選填金鑰；尚未抓取清單時顯示已知 embedding 模型的預設清單；兩個本地供應商提供本地伺服器自動探測按鈕；並提供「測試連線與向量生成」按鈕，回報向量維度與耗時。
   * 模型選擇彈窗具備一鍵清除按鈕與主流模型家族快捷過濾標籤（Gemini、GPT、Claude、DeepSeek 等）。
5. **獨立除錯分頁與日誌開關**
   * 獨立的**除錯**設定分頁，含「詳細日誌」核取方塊（預設關閉——每次請求都會寫一行日誌，而 Verse 的共用日誌上限為 10000 筆），讓 Mod 開發者與玩家在排查問題時自由開關本 Mod 逐次請求的日誌輸出。一次性的警告（例如 API 金鑰無法解密、遙測寫檔失敗）則一律記錄。
6. **一鍵連線測試**
   * 即時連線檢查，量測延遲並驗證 API 金鑰與模型。在基底類別實作一次，所有供應商共用。
7. **執行緒安全與主執行緒 Scribe 派送**
   * 所有設定字典皆以鎖保護，防止多執行緒並發讀寫。
   * `RecordLog` 的記憶體內日誌更新仍透過 `RimLLMDispatcher` 在 Unity 主執行緒執行，遙測寫檔（AES 加密＋JSON 序列化＋磁碟寫入）則由背景單寫者執行，並套用 15 秒寫入節流，避免背景存檔造成崩潰或 TPS 掉幀。
8. **推理模型與思維鏈標記**
   * 原生支援 **Gemini 3.7 Flash / 3.1 Pro (Thinking)**、**OpenAI GPT-5.6 Sol / GPT-5.5**、**DeepSeek-V4-Pro / Flash**、**Grok 4.6**、**Qwen3.8-Max**、**Kimi K3**、**GLM-5.3-Flash** 等現代深度推理與思考模型。
   * 框架會把 API 回傳的思維鏈（OpenAI 協定的 `reasoning_content`）正規化成 MEAI 原生的 `TextReasoningContent`，放在 `ChatResponse.Messages` 與 `ChatResponseUpdate.Contents` 裡交給你。它刻意**不**被揉進 `ChatResponse.Text`：否則每一個讀 `Text` 的呼叫端（結構化輸出、JSON 解析）都得先把標籤剥掉。要保留或丟棄，依內容型別過濾即可。
   * GUI 對話測試頁自己從這些內容組出 `<think>...</think>` 標籤，再將思維鏈以灰色斜體呈現。那個扁平化是呈現，不是協定。
   * **推理強度控制**：預設為「自動」，維持服務端自己的預設行為（OpenAI 的動態 `reasoning_effort` 等）。也可以完全關閉推理，或手動設為低／中／高。
   * **強度對所有供應商、所有模型都有效**。線上格式由各供應商自行宣告，框架不再靠模型名猜測：頂層 `reasoning_effort`（OpenAI、經 OpenAI 相容端點存取的 Gemini、xAI、Groq、MiniMax、NVIDIA、OpenAI 相容端點）、OpenRouter 的統一 `reasoning` 物件、`thinking: {type}` 加強度（DeepSeek、Z.ai、Kimi）、`enable_thinking` 搭配 `thinking_budget`（Qwen）。詞彙差異逐家對應 —— Kimi 只吃 low/high/max，xAI 的推理無法關閉，關閉請求在該家會被忽略而不是換來 400。
   * **未知模型先樂觀送出，再從服務端學習**。以模型名列白名單必然腐化：框架先前只對 `o1`/`o3` 開頭的模型送出強度，其餘一律靜默丟棄。現在除了少數已知不具思考能力的系列之外一律送出；若服務端以 400 拒絕該參數，框架會記下這組 (供應商, 模型)、去掉參數重打一次，並在本次遊戲執行期間不再送。漏掉一個模型的代價因此是一次重試，而不是永久失效。同一套機制也涵蓋 `temperature` —— GPT-5 等推理模型會直接拒絕它。「關閉思考」被拒（對 o 系列這類關不掉的模型送 `reasoning_effort: "none"`）會另外記：之後只略過該模型的關閉指令，明確指定的強度仍照常送出——連線測試一律要求關閉思考，先前那一次 400 會讓玩家設定的強度整個 session 都被靜默丟掉。記憶只存在於本次執行，模型日後支援了，重開遊戲就會重新嘗試。
   * **Markdown 呈現**：對話測試頁採用 **Markdig AST 解析器** 將模型回覆精準轉成 Unity 舊版 rich text，標題、粗體、斜體、清單、引用、連結與程式碼區塊會以結構呈現，而不是印出 `**`、`` ` `` 這些原始符號。舊版 IMGUI 只認得 `b`、`i`、`size`、`color`、`material`、`quad` 六個標籤，沒有對應標籤的結構（縮排、表格）以空白與符號近似。底線斜體刻意不支援，因為會與 `snake_case` 識別字衝突。
9. **上下文快取與 Prompt 快取**
   * 在 `RimLLMChatOptions` 設定 `CachedContext`，框架會把它併入系統訊息，具備服務端 prompt caching 的供應商（OpenAI，以及經 OpenAI 相容端點存取的 Gemini）會對重複前綴自動打折，大幅降低高頻重複請求的輸入 Token 成本與延遲。
   * **量化節省**：用量統計會解析 API 回傳的快取命中 Token（OpenAI `cached_tokens` 及其等價欄位），並依費率表中該模型的快取輸入估計費率計算。供應商定價可能變動，因此金額仍是估算值。
   * **成本估算來自內建費率表**（OpenAI、Gemini、DeepSeek、Groq、Qwen、Kimi、MiniMax、Z.ai 與 xAI 模型；`gpt-4o-2024-11-20` 這類帶日期的變體會對到基底模型）。查無費率代表**費用未知**，不是已知免費：token 數仍會累計，但其費用不列入顯示總額與每日預算。Debug 分頁會顯示本次執行中查無費率的請求數。每日預算在請求前檢查已累計的估算金額，並非精確的消費上限。
10. **Embedding SDK**
    * 框架公開由 Google Gemini、OpenAI、Ollama 或 OpenAI 相容端點支援的 embedding 功能。其他 Mod 可透過 `RimLLMProvider.CreateEmbeddingGenerator` 取得標準 `IEmbeddingGenerator`，用於語意檢索與分群。
    * 所有線上來源都走 OpenAI SDK：Google 經官方 OpenAI 相容端點存取 Gemini，OpenAI 走其原生端點；Ollama 與自架服務使用 OpenAI SDK 的 `EmbeddingClient`（Ollama 走其 OpenAI 相容的 `/v1` 端點）。因此「Embedding 端點」欄位填的是**服務根位址**（如 `http://localhost:11434/v1`）；填入完整 `/embeddings` 路徑會自動正規化。模型、端點與金鑰依 Embedding 供應商分別保存，切換啟用的供應商不會遺失其他供應商的設定；模型或端點留空代表使用該供應商預設值，金鑰留空則繼承對應對話供應商的金鑰。
    * 設定頁可直接抓取可用模型清單，不必憑記憶輸入名稱；只要伺服器說得出哪些是 embedding 模型，清單就**只留真正的 embedding 模型**：Google 走原生 `/models` 清單（`supportedGenerationMethods` 含 `embedContent`）、Ollama 看 `/api/show` 的 `capabilities`、LM Studio 看 `/api/v0/models` 的 `type`，OpenAI 則依官方型錄固定的 `text-embedding-*` 命名過濾。只有兩者皆無的通用 OpenAI 相容伺服器才退回沒有能力資訊的 `/v1/models`，此時清單只**排序**（把像 embedding 的名稱排前面）而不過濾 —— 伺服器的模型名可能由使用者自訂，過濾會把合法選項藏起來；狀態列會明講清單未過濾。沒有模型清單端點的伺服器仍可手動輸入。
    * Embedding 屬計費 API，因此與一般生成請求共用同一套防濫用檢查；其金鑰採用與供應商金鑰相同的 AES 加密。
11. **原生 Tool Calling（函式呼叫）**
    * 完整支援 Microsoft.Extensions.AI Tool Calling 標準（`AIFunction`、`ChatOptions.Tools`、`FunctionCallContent`、`FunctionResultContent`），所有供應商經共用的 OpenAI 協定路徑提供。
    * 提供 `RimWorldFunctionInvoker.AsMainThreadFunctionInvokingClient()`，自動將工具叫用委派排入 Unity 主執行緒執行，杜絕 RimWorld 跨執行緒崩潰風險。
12. **第三方整合（強制其他 Mod 改走 RimLLM）**
    * 設定頁新增「第三方整合」分頁，列出 RimLLM 可以接管 LLM 流量的 Mod——目前包含 **RimTalk**（`cj.rimtalk`）、**Auto Translation**（`seohyeon.autotranslation`）與 **Mod 兼容性檢查器**（`modcompatchecker.main`）。開關開啟後，目標 Mod 的所有請求一律導入 RimLLM 的備援鏈、預算、節流與用量統計。開啟期間目標 Mod 自己的 API 金鑰／模型／端點設定會被忽略；開關即時生效不需重啟，預設**關閉**。
    * **RimTalk**：提示工程完全不動，訊息（含「Output JSONL」指示）原樣轉送，串流文字逐塊餵進 RimTalk 自己的 `JsonStreamParser`，氣泡仍然像原生一樣一行一行冒出來。推理內容不會混進 JSONL 串流，且這類請求關閉思考以對齊 RimTalk 自身的預設。
    * **Auto Translation**：完整支援單條與 XML 批次翻譯。以原生具備批次能力與佔位符防護的 `Translator_OpenAICompatible` 作為哨兵轉接，佔位符（`__PH0__` 等）防護、XML 批次打包與解析均沿用 Auto Translation 自身邏輯。開關開啟時自動同步當前翻譯器為哨兵，關閉或 RimLLM 缺金鑰/離線時自動退回原生翻譯引擎並節流警告。
    * **Mod 兼容性檢查器**：攔截 `AIService.CallAPIWithTimeout`，將 Harmony/XML 衝突分析、依賴問題與報錯診斷等請求全面導流至 RimLLM。接管開啟時自動將 `IsAIConfigured` 覆寫為 `true`，玩家無需在檢查器內重複設定金鑰即可直接使用 AI 診斷，並自動短路餘額查詢以杜絕 401 報錯。
    * 純 Harmony、全部 fail-soft，框架不實作任何第三方介面，也絕不在類別繼承、介面或欄位層級引用第三方型別，第三方 Mod 未安裝時框架組件仍可安全載入。其 API 若漂移導致掛載失敗，設定頁會直接顯示原因。請求當下 RimLLM 沒有可用供應商（備援鏈為空、缺金鑰）時，自動退回原生路徑並記一筆節流過的警告。
    * 新增其他 Mod 只需一筆登錄＋一個 `RimLLMCompatTarget` 子類；轉接器對著簽入版本庫的參考用 DLL（`Source/Libs/`）編譯，該 DLL 不隨包出貨。

---

## 🛠️ 架構設計

### 1. 統一介面與調度核心（`IChatClient` / `IEmbeddingGenerator` 與 `RimLLMProvider`）

* 框架對外暴露標準的 Microsoft.Extensions.AI 介面。呼叫端只面對 `IChatClient` 或 `IEmbeddingGenerator`，完全不需要知道實際由哪個供應商或模型處理 —— 調度與 Fallback 輪替由 `RimLLMManager` 負責。
* `CreateChatClient` 回傳的是一疊 MEAI `DelegatingChatClient` 中介層——思考強度正規化、防濫用節流、預算保護、優先權佇列——最內層是沿 Fallback 鏈路由的 `FailoverChatClient`。每一層都是 `internal`。使用端會碰到的框架專屬型別只有 `RimLLMProvider`、`RimLLMChatOptions`、`RimLLMException` 與 `LLMError`，其餘跨越邊界的全是 MEAI 型別。
* `modId` 是純標籤，不是憑證。它是每個 Mod 防濫用節流與遙測歸屬的鍵，不需要任何註冊呼叫。

### 2. Unity 主執行緒派送器（`RimLLMDispatcher`）

* 網路請求在背景執行緒池上非同步執行，但多數 Unity API 與 RimWorld 邏輯並非執行緒安全 —— 從背景執行緒呼叫會造成崩潰或 TPS 掉幀。
* `RimLLMDispatcher` 是 MonoBehaviour 單例，以 `ConcurrentQueue` 收集背景執行緒的回呼，並在 Unity 每幀的 `Update` 中派送回主執行緒。

### 3. 串流橋接（`Channel<T>`）

* Executor 的串流 API 是回呼形式（`Func<ChatResponseUpdate, Task> onUpdateReceived`），而 MEAI 要的是 `IAsyncEnumerable<ChatResponseUpdate>`。兩者之間以**有界**的 `System.Threading.Channels.Channel<T>`（64 筆 update）橋接，生產端 await `WriteAsync`，因此消費端慢時會把背壓回推到網路讀取，而不是把整段串流緩衝在記憶體。消費端是 `ChannelReader.ReadAllAsync()`。
* update 以**原樣**穿過這座橋——你列舉到的就是供應商產生的那個物件，只有 `ModelId` 被改寫。框架不再自行合成一個收尾 update，因此 `UsageContent`、`FinishReason` 與 `ResponseId` 只在供應商真的送出時才存在。
* 由於 `IAsyncEnumerable` 是透過 `bclasync` extern alias 進入本專案，C# 8 無法對它編譯 async iterator。`ReadAllAsync()` 直接繞過這個限制：它回傳的正是同一顆組件的 `IAsyncEnumerable`，因此不必手寫任何 iterator。
* 一層薄包裝會解開 `ChannelClosedException`，讓生產端的失敗以原始的 `RimLLMException` 呈現給呼叫端。
* Executor 的 fallback token 估算逐塊累加成計數，不暫存串流文字，異常大的串流不會讓記憶體無限成長。

### 4. 統一的 HTTP 錯誤對照（`LLMErrorMapper`）

* HTTP 狀態碼轉換為 `LLMError` 的規則集中在 `LLMErrorMapper` 一處，由官方 SDK 路徑（`ClientResultException`）與 embedding 服務共用。
* `Retry-After` 的解析也在其中，基於 `RetryConditionHeaderValue`，因此延遲秒數與 HTTP 日期兩種格式在各處行為一致。
* 這使得「哪些狀態碼可重試」與「哪些代表 Schema 遭拒應降級」在各處行為完全相同。第三方自訂供應商也能引用同一份對照。

### 5. 容錯的結構化輸出（structured output 與 JSON repair）

* 開發者經常需要模型回傳特定的 JSON 結構。
* 內建供應商優先使用官方 SDK 的原生結構化輸出，經 `IChatClient` 的 JSON Schema response format 送出（Gemini 經其 OpenAI 相容端點同樣適用）。框架會先驗證必要成員與 null 狀態，再反序列化為目標 C# 物件。
* schema 本身只有一種 OpenAI 相容方言 —— 所有成員都列入 `required`，選填性以 `["integer","null"]` 聯集表達。詳見[架構設計 §6](#6-官方-sdk-與供應商職責)。
* `RepairJson` 回退機制僅在供應商不支援原生 Schema、服務拒絕 Schema，或模型仍回傳格式錯誤內容時啟用。它處理 Markdown 圍籬（如 ` ```json `）、未閉合括號、尾隨逗號與 JSON 區塊擷取。

### 6. 官方 SDK 與供應商職責

* 主專案與測試專案維持 `net472`；RimWorld Mod 不需要遷移到 .NET 8。官方 SDK 的相依 DLL 隨 Mod 發佈，並由 `ProviderSdkIntegrationTests` 逐一載入並反射，讓遺漏的間接相依組件在建置階段就失敗而不是在遊戲裡。要注意這項檢查跑在真正的 .NET Framework 上，因此拓不到「在這裡存在、但 RimWorld 的 Mono BCL 沒有」的型別 ——下方的 `DataAnnotations` 就是這種失敗，因此改以檢查出貨組件的參考清單來釘住，而不是靠執行程式碼。雖然 .NET Framework 將 `System.ValueTuple` 視為框架組件，建置仍明確部署其 `4.0.5.0` DLL，以避免 RimWorld 的 Mono 反射 MEAI 時發生 `ReflectionTypeLoadException`。
* **OpenAI** 使用 `OpenAI` SDK `2.13.0` 搭配 `Microsoft.Extensions.AI` / `Microsoft.Extensions.AI.OpenAI` `10.10.0`。針對 OpenAI SDK 2.13.0 與 `System.ClientModel` 1.15.0 在實驗性 `ChatCompletionOptions.Patch` API 內部因 `PropagateSet` 缺乏 null 防護而擲出 `NullReferenceException` 的問題，框架透過 `OpenAIPatchExtensions.DisablePatchPropagators()` 清除傳播委派，安全恢復底層 JSON Patch 寫入機制以注入 `reasoning_effort`、`response_format`、`max_tokens` 與 `models` 欄位。內建的 `OpenAIProvider` 透過 `ChatClient.AsIChatClient()` 進入共用 manager。只有真正實作 OpenAI Chat Completions 協定的端點（LM Studio、Ollama、vLLM…）才適合 OpenAI 相容轉接。
* **Gemini** 經 Google 官方 OpenAI 相容端點（`https://generativelanguage.googleapis.com/v1beta/openai/`）存取，文字、串流、原生 Schema、思考與工具呼叫全數重用共用的 `OpenAIProvider` 實作。Gemini 只是宣告端點與預設測試模型的薄子類 —— 與 Groq、Qwen 等供應商同形。
* **所有內建供應商都走 OpenAI SDK**：整個家族（OpenAI、Gemini、OpenRouter、DeepSeek、Groq、Grok、Z.ai、Kimi、MiniMax、Qwen、NVIDIA、OpenAICompatible）使用 `OpenAI` SDK `2.13.0` 加 MEAI 的 `IChatClient`。模型清單使用 `OpenAIModelClient.GetModelsAsync()`，而非自行拼 `/models` URL 再解析 JSON。
* **框架已無任何 raw HTTP 路徑，也不再有第二套 SDK。** 移除最後的原生 `Google.GenAI` 路徑後，整個第二套傳輸層、認證處理，以及 Google `cachedContents` 顯式快取機制一併刪除：`CachedContext` 一律以系統訊息內文送達，快取與否由供應商在服務端對重複前綴自行處理。
* **JSON Schema 產生走 `System.Text.Json` 的 `JsonSchemaExporter` 加一層正規化**（`RimLLMSchemaBuilder`），分三階段。**Stage A** 由 exporter 匯出完整 JSON Schema。**Stage B** 正規化成所有供應商都接受的受限子集：解析並展開 `$ref` 指標、截斷循環與過深巢狀、把可為 null 的聯集收斂成單一 `type`、只保留關鍵字白名單。**Stage C** 套用唯一的 OpenAI 相容方言，選填成員寫成 `["integer","null"]` 聯集。
  * **`Microsoft.Extensions.AI.Abstractions` 出貨的是 `netstandard2.0` 版本，而不是 NuGet 依 `net472` 自動挑的 `net462` 版本。** `net462` 版會讀 `[EmailAddress]`、`[Range]` 之類的驗證屬性豐富 schema，那段程式碼參考框架內建的 `System.ComponentModel.DataAnnotations`。RimWorld 的 Unity Mono 沒有出貨那顆 DLL，所以遊戲內 `AIFunctionFactory.Create` 與 `AIJsonUtilities.CreateJsonSchema` 曾擲出 `TypeLoadException: Could not resolve type … 'EmailAddressAttribute' in assembly 'System.ComponentModel.DataAnnotations, Version=4.0.0.0'` —— 連無參數的工具也炸，而單元測試跑在有 GAC 的真 .NET Framework 上完全看不出來。`netstandard2.0` 版整段以 `#if NET || NETFRAMEWORK` 排除，相依只剩 `System.Text.Json`，組件版本同為 `10.10.0.0`，因此 `Microsoft.Extensions.AI` 與 `Microsoft.Extensions.AI.OpenAI` 的綁定不受影響。csproj 顯式引用該版本；`ShippedAbstractionsHasNoDataAnnotationsDependency` 檢查出貨 DLL 的參考清單，選擇一旦被改回就會失敗。下游 Mod 不受影響：它們對套件編譯、對框架出貨的 DLL 執行。
  * **仍然直接呼叫 exporter，不經過 MEAI 的 `AIJsonUtilities.CreateJsonSchema` 包裝層。** 它就是 MEAI 內部使用的同一個引擎；直呼讓 Stage B 拿到未經包裝層改寫的原始輸出，正規化只需要對付一種形狀。MEAI 唯一多做而仍需要的 `[Description]`，由 Stage B 自行讀取。
  * 直呼 exporter 有兩個後果：列舉只會輸出 `{"enum":[…]}` 而不帶 `type`（Stage B 由列舉值反推型別，否則所有列舉成員都會消失），而且它完全沒有 `description` 的概念（Stage B 自行讀取成員與類別上的 `[Description]`）。
  * **循環在 CLR 型別層截斷，而非 JSON pointer 層。** exporter 會把遞迴成員先完整展開一輪、其中才出現指回祖先的 `$ref`，只靠 pointer 偵測就會多送一整層 —— 實測遞迴測試型別從 789 字元漲到 3119 字元，而那是每次請求都要付的 prompt token。由 `RecursiveSchemaStaysCompact` 守住。
  * **巢狀深度上限跟隨 strict structured output 的上限。** OpenAI 的 strict structured output 最多允許 5 層巢狀（另有全域 100 個 property 的上限），超過會被服務端拒絕並靜默降級成提示式 JSON，因此產生器在 5 層截斷。注意 100 個 property 的上限目前**尚未**強制。
  * exporter 的原始輸出不能直接送：可為 null 的成員會寫成 `["string","null"]` 聯集，送出前必須先正規化；`$ref` 指標也必須先解析 —— 這兩點由單元測試覆蓋，不再只是本文件裡的一句宣稱。
  * `$ref` **不只**用於遞迴 —— MEAI 也用它來為重複出現的型別去重，所以一律截斷 `$ref` 會靜默刪掉正常成員。正規化層會解析 JSON pointer，只有在它指向目前展開路徑上的祖先時才視為循環。
  * **所有成員一律列入 `required`**，選填性改由型別表達。OpenAI 的 strict structured output 要求 `required` 涵蓋每一個 property，所以舊行為（`Nullable<T>` 不列入 `required` 卻仍送 `strict: true`）在服務端會被拒絕，並被靜默降級成提示式 JSON。
  * schema 產生與反序列化都跑在 System.Text.Json 的單一共用契約下（納入欄位、尊重 `[JsonIgnore]`／`[JsonPropertyName]`、排除唯讀成員），並有測試斷言兩邊成員集合一致。**結構化輸出的型別請勿使用自訂的 `JsonConverter`** —— 它會改變 wire 形狀，而 exporter 看不到。子 mod 遷移注意：Newtonsoft 的 `[JsonProperty("x")]`須改為 STJ 的 `[JsonPropertyName("x")]`，`Newtonsoft.Json.JsonIgnoreAttribute` 須改為 `System.Text.Json.Serialization.JsonIgnoreAttribute` —— 遷移後舊標註會被靜默忽略。
  * `JsonSchemaExporter` 已實測可在 RimWorld 的 Mono 環境運作，因此不再保留反射降級路徑；若它失敗，結構化請求會直接拋出例外而不是靜默降級。遊戲內可用偵錯分頁的結構化輸出自我檢查確認。
* 供應商專屬 SDK 絕不出現在 `RimLLMManager` 或公開 SDK facade 中；共用層只相依 `IChatClient`、`LLMProviderCapabilities` 與既有的 `ILLMProvider` API。API 金鑰一律來自加密設定，絕不寫入原始碼或一般日誌。

---

## 🔐 安全性說明

為避免誤解，以下誠實說明每項安全機制實際防護的範圍：

* **API 金鑰加密使用 OS 每使用者保護金鑰。** 新資料使用 AES-256 與隨機儲存金鑰，該金鑰由目前使用者的 OS 保護機制包裝。舊版 `v1`／`v2` 密文仍以裝置衍生素材解密，但只用於遷移；新資料不再從原始碼固定種子或 `deviceUniqueIdentifier` 衍生儲存金鑰。這能防止其他使用者或其他機器直接解密複製的設定，但**無法**防禦以相同使用者身分或在同一 RimWorld 行程內執行的程式碼（包含其他 Mod），因為框架必須使用金鑰才能提供請求服務。
* **`modId` 是歸屬標籤，不是身份驗證。** 每 Mod 節流仍用來維持合作型 Mod 之間的公平性；另外加上「每 Mod 視窗上限十倍」的共享安全上限，每個實際供應商呼叫（包含工具迴圈續輪）都會計入，避免輪換標籤建立無限供應商請求。同一個 RimWorld 行程內的 Mod 仍沒有隔離，請勿把 SDK 當成惡意 Mod 沙盒。
* **金鑰不會進入 URL 或日誌。** 所有供應商都以 HTTP Header 傳遞金鑰；日誌輸出一律經過 `SanitizeForLog` 並截斷長度，診斷匯出中的裝置識別碼也會遮罩。
* **供應商輸出視為不受信任的 UI 輸入。** ChatTest 只保留框架自行產生的灰色思考色彩標籤；供應商回應中的 raw HTML 標籤會以文字顯示，再交給 Markdown/Unity IMGUI。

---

## 📜 授權條款

本模組原始碼以 **MIT License** 釋出 —— Copyright (c) 2026 **mushroomTW**。詳見 [LICENSE](LICENSE)。

隨附於 `Assemblies/` 的相依組件維持各自的授權：Microsoft.Extensions.AI 與 OpenAI .NET SDK 為 MIT。RimWorld 本身的組件屬於 Ludeon Studios，本模組不予散布。

---

## 🧪 單元測試與驗證

專案在 `Source/RimLLM Framework.Tests`（與主專案並列的獨立專案）附有完整的單元測試套件，涵蓋 AES 加解密、模型 Fallback、JSON Schema 產生（正規化）與修復、HTTP 錯誤對照、`Retry-After` 解析、`ChatOptions` 複製、串流重試與預算控制。

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

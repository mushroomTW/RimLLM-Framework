# SDK 對標 Microsoft.Extensions.AI 設計文件

- 日期：2026-08-07
- 狀態：已審核（使用者確認方案 A：Manager 包成 `IChatClient` facade，硬切換不留相容層）
- 對應主題：將 `RimLLM_Framework.SDK` 自訂呼叫面（`IRimLLM`／`LLMRequest`）對標為 MEAI 慣例，第三方 mod 以標準 `IChatClient`／`IEmbeddingGenerator` 使用框架

---

## 1. 目標與範圍

### 1.1 背景問題

框架內部實作已全面走 MEAI `IChatClient`（見 `docs/superpowers/specs/2026-08-07-provider-sdk-unification-design.md` 已完成之任務），但對外 SDK facade 仍是自訂 API：

- `RimLLMProvider.Instance` + `IRimLLM`（`GenerateAsync`／`GenerateObjectAsync`／`StreamAsync` 等 9 個方法）
- `LLMRequest`（含 fluent builder 與 `LLMReasoningEffort` enum）
- `RimLLMAiOptions`（死碼，全庫僅定義、無任何使用）

這造成：第三方 mod 需學習兩套 API（SDK 自訂 + MEAI），框架亦需平行維護兩套 API 表面。

### 1.2 目標

1. 移除自訂呼叫面（`IRimLLM`、`LLMRequest`、`RimLLMAiOptions`、`RimLLMProvider.Instance`）。
2. 第三方 mod 以標準 MEAI `IChatClient`／`IEmbeddingGenerator` 使用框架。
3. 框架核心價值（Fallback Chain、預算、佇列、防濫用、用量統計、provider 註冊）**全部保留在回傳的 `IChatClient` 內部**，不允許繞過。
4. 結構化輸出以擴充方法提供（MEAI 10.8.3 無內建 `GetResponseObject<T>`，反序列化與 JSON repair 留在框架）。
5. 硬切換：不留 deprecated 相容層（目前無已知第三方 mod 使用）。

### 1.3 非目標

- 不變更內建 provider 的 SDK 整合（上一個任務已完成的 `IChatClientFactory` 層不動）。
- 不升級或變更既有套件版本（OpenAI 2.12.0、MEAI 10.8.3、Google.GenAI 1.16.0）。
- 不處理 `IRimLLMSettings`／`RimLLMFrameworkSettings` 的設定 UI 行為（僅型別位置可能調整）。
- 不新增 MEAI middleware 的依賴（`ChatClientBuilder` 生態由第三方自行組合；框架不內建 `.UseFunctionInvocation()` 等）。

---

## 2. 新架構

### 2.1 SDK 公開 API 表面

**`RimLLMProvider`（靜態入口，重寫）**

| 成員 | 說明 |
|---|---|
| `void RegisterClient(string modId)` | 保留現行防濫用註冊（`Assembly.GetCallingAssembly()` 綁定） |
| `IChatClient CreateChatClient(string modId)` | 回傳綁定 mod 的 facade，內部走 manager 調度 |
| `IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(string modId)` | 對標 MEAI，取代 `GetEmbeddingAsync`／`GetEmbeddingsAsync` |
| `void RegisterProvider(ILLMProvider provider)` | 保留（第三方自訂 provider 契約不變） |
| `Task<TestResult> TestProviderAsync(string providerId)` | 原 `IRimLLM` 方法改為靜態直通 manager |
| `Task<List<string>> FetchProviderModelsAsync(string providerId)` | 同上 |
| `List<string> GetRegisteredProviderIds()` | 同上 |

**新增型別**

- `RimLLMChatOptions : ChatOptions`
  - 框架專屬參數：`Priority`（int）、`MinFallbackLevel`（string）、`CachedContext`（string）、`EnableContextCaching`（bool）、`OnStreamRestart`（Action）
  - 不傳亦可（用預設值），純 MEAI 使用者不需要知道此型別。
- `RimLLMClientExtensions.GetResponseObjectAsync<T>(this IChatClient client, IEnumerable<ChatMessage> messages, RimLLMChatOptions options = null, CancellationToken ct = default)`
  - 內部建立 `ChatResponseFormat.ForJsonSchema(...)`、走既有 RepairJson + 反序列化。
  - Schema 產生自動快取（取代 `RegisterResponseType<T>`，不保留獨立 API）。

**移除**

- `IRimLLM`（含 `GenerateObjectAsync`、`GenerateStreamingAsync`、`RegisterResponseType`）
- `LLMRequest`（fluent builder 由 options 取代）
- `LLMReasoningEffort` enum（改用 MEAI `ChatOptions.Reasoning` 原生；None 與 Auto 由框架依 provider 慣例轉換）
- `RimLLMAiOptions`（死碼）
- `RimLLMProvider.Instance`

**保留（公開）**

- `ProviderIds`、`LLMError`、`RimLLMException`、`TestResult`
- `ILLMProvider`（第三方自訂 provider 契約）
- `IRimLLMSettings`、`LLMProviderCapabilities`
- `IChatClientFactories`（`IOpenAIChatClientFactory`／`IGeminiChatClientFactory`）

### 2.2 `RimLLMChatClient`（facade 實作）

```
RimLLMChatClient : IChatClient
├─ 建構：接收 RimLLMManager + modId（建立時以 Assembly.GetCallingAssembly() 綁定防濫用）
├─ GetResponseAsync(IEnumerable<ChatMessage>, ChatOptions, CancellationToken)
│   1. options 為 RimLLMChatOptions 時取出 Priority/MinFallbackLevel/CachedContext 等
│   2. ChatOptions.ModelId 指定 → 優先該 model（仍走 fallback 降級下限）
│   3. 未指定 → 走既有 Fallback Chain / 路由策略
│   4. ResponseFormat 是 ForJsonSchema → 原生結構化輸出路徑
│   5. 既有流程不變：預算檢查 → 佇列 → provider 選擇 → executor
│   6. 組 ChatResponse：Text（含 <think> 封裝維持既有格式）、Usage（既有統計）、
│      ModelId、RawRepresentation 透傳
├─ GetStreamingResponseAsync → IAsyncEnumerable<ChatResponseUpdate>
│   走既有 StreamProviderAsync；fallback 中途接手時觸發 RimLLMChatOptions.OnStreamRestart
└─ Metadata → ChatClientMetadata { ProviderName = "RimLLM", ProviderUri = null, DefaultModelId = null }
```

**MEAI ↔ 框架對映**（沿用既有行為）

| MEAI 概念 | 框架現有機制 |
|---|---|
| `ChatOptions.Temperature` / `MaxOutputTokens` / `Reasoning` | 既有 executor `BuildOptions`（provider hook 套用） |
| `ChatOptions.ModelId` | provider/model 選擇 + fallback |
| `ChatOptions.ResponseFormat` | 原生 JSON Schema 輸出（`SupportsNativeStructuredOutput` 旗標、schema 拒絕降級重打不變） |
| `ChatOptions.AdditionalProperties` | 合併進 executor 的 `AdditionalProperties`（`IChatOptionsCustomizer` 逃生門保留） |
| `ChatResponse.Usage` | 既有 `UsageDetails` → 用量統計／預算扣減 |
| `ChatResponse.Text` | `<think>` 封裝維持既有組裝規則 |
| `ChatResponseUpdate` | 既有串流 chunk 解析 |
| `Reasoning`（`ReasoningOptions.Effort`） | 取代 `LLMReasoningEffort`；None/Auto 由 provider hook 依慣例轉換（沿用現行 o1/o3 `reasoning_effort` 處理） |

**錯誤**：`RimLLMException`（含 `LLMError` 統一錯誤碼）維持為主要例外型別；MEAI 對例外型別無約束，第三方 catch `RimLLMException` 沿用現有錯誤處理慣例。

### 2.3 `RimLLMEmbeddingClient`

- `RimLLMEmbeddingClient : IEmbeddingGenerator<string, Embedding<float>>`
- 內部接到既有 `RimLLMEmbeddingService`（線上供應商 + 防濫用）
- `EmbeddingGenerationOptions` 僅讀 `ModelId`（指定供應商時用），其餘忽略

### 2.4 內部重構

- **`RimLLMManager`**：移除 `IRimLLM` 實作；新增 `CreateChatClient(string modId)`／`CreateEmbeddingGenerator(string modId)`；既有流程（預算、佇列、fallback、路由、防濫用、用量）全部不變，僅新增「ChatMessage 清單 + ChatOptions → 內部請求」的轉譯層取代 `LLMRequest` 角色。
- **`RimLLMChatClientExecutor`**：`LLMRequest` 參數改為轉譯層請求物件；`BuildOptions` 改為與使用者提供的 `ChatOptions` 合併（**使用者優先、框架補缺**）；`AdditionalProperties` 合併。
- **`ClientRegistry`**（防濫用）：維持，改由 `CreateChatClient` 建立時綁定 mod（而非每次請求驗證）。

---

## 3. 行為對照表（全數保留）

| 既有 API | 新 API | 備註 |
|---|---|---|
| `GenerateAsync(LLMRequest)` / `GenerateAsync(modId, prompt, ...)` | `client.GetResponseAsync(messages, options)` | 未指定 ModelId 走 fallback chain |
| `GenerateObjectAsync<T>(LLMRequest)` | `client.GetResponseObjectAsync<T>(...)` | schema 自動快取 |
| `StreamAsync(LLMRequest, Action<string>)` | `client.GetStreamingResponseAsync(...)` | restart 由 `RimLLMChatOptions.OnStreamRestart` 通知 |
| `GenerateStreamingAsync(modId, prompt, ...)` | 同上 | — |
| `GetEmbeddingAsync` / `GetEmbeddingsAsync` | `generator.GenerateAsync(...)` | `IEmbeddingGenerator<string, Embedding<float>>` |
| `TestProviderAsync(providerId)` | `RimLLMProvider.TestProviderAsync(providerId)` | 靜態直通 |
| `FetchProviderModelsAsync(providerId)` | `RimLLMProvider.FetchProviderModelsAsync(providerId)` | 靜態直通 |
| `GetRegisteredProviderIds()` | `RimLLMProvider.GetRegisteredProviderIds()` | 靜態直通 |
| `RegisterProvider(ILLMProvider)` | `RimLLMProvider.RegisterProvider(...)` | 不變 |
| `RegisterClient(modId)` | `RimLLMProvider.RegisterClient(modId)` | 不變 |
| `RegisterResponseType<T>()` | 移除 | `GetResponseObjectAsync<T>` 自動快取 |
| `LLMReasoningEffort` | `ChatOptions.Reasoning` | None/Auto 由 provider hook 轉換 |

框架功能對應：fallback（ModelId 指定→優先+降級；未指定→chain）、預算／佇列／防濫用／用量（manager 流程不變）、CachedContext／OnStreamRestart（進 `RimLLMChatOptions`）。

---

## 4. 資料流

1. 第三方 mod：`RimLLMProvider.RegisterClient(modId)` → `CreateChatClient(modId)` → `GetResponseAsync(...)`。
2. `RimLLMChatClient` 依 `ChatOptions`（ModelId／ResponseFormat／RimLLMChatOptions）組內部請求。
3. Manager 既有流程：預算檢查 → 佇列 → fallback 選擇 provider → `CreateChatClient(model)`（provider 層）→ executor。
4. Executor：`BuildMessages`（system + user）→ `BuildOptions`（合併使用者 ChatOptions + provider hook）→ `GetResponseAsync`／`GetStreamingResponseAsync`。
5. 回應解析：text + reasoning（`<think>` 封裝）+ usage（`RecordUsage`）→ 組 `ChatResponse`。
6. 原生 schema 被拒絕時，manager 既有降級流程不變（提示式 JSON + RepairJson）。

---

## 5. 錯誤處理

- `RimLLMException`（`LLMError` 統一錯誤碼）維持主要例外契約，manager 既有對照流程不變。
- 防濫用未註冊／assembly 不符：維持既有阻斷行為（例外訊息同現行）。

---

## 6. 測試策略

- **`FrameworkTests.cs`**：約 40+ 處 `LLMRequest`／`RimLLMProvider.Instance` 用法全面改為 `IChatClient` 慣例。
- **`RimLLMChatClientExecutorTests.cs`**：改用 `ChatOptions` 驗證（ReceivedOptions 內容：ModelId、Temperature、MaxOutputTokens、ResponseFormat）。
- **新增 `RimLLMChatClientTests`**：facade 行為——ModelId 指定與否的 provider 選擇、fallback、串流 restart 觸發、usage 對映、`RimLLMChatOptions` 傳遞、`AdditionalProperties` 合併、純標準 `ChatOptions` 的預設值路徑。
- **新增 `RimLLMEmbeddingClientTests`**：對 `IEmbeddingGenerator` 的呼叫與防濫用。
- **`ProviderSdkIntegrationTests.cs`**：改為驗證 `CreateChatClient` 流程與 SDK 型別可載入。
- 測試前置需求與執行方式（`dotnet build`／`dotnet test`）不變。

---

## 7. 實作前 Spike（Phase 1）

下列項目須在遷移前以實際程式碼驗證，結果會決定最終細節：

1. 既有 executor 的 `AdditionalProperties` 透傳方式：一般 `ChatOptions.AdditionalProperties` 是否直接透傳到 OpenAI SDK body，還是僅靠 `RawRepresentationFactory` 逃生門（決定 facade 合併方式）。
2. MEAI `ReasoningOptions.Effort` 對 `reasoning_effort` 字串的對映與列舉值（現行 o1/o3 處理的遷移方式）。
3. `RimLLMChatClient.GetStreamingResponseAsync` 在 net472 + `Microsoft.Bcl.AsyncInterfaces` alias 下的編譯與執行（測試專案已驗證 IAsyncEnumerable 可用，facade 需同模式）。

Spike 產物：每個項目標註「可行／不可行／替代方案」。不可行的項目回到使用者協商替代方案。

---

## 8. 檔案變更清單

**SDK 資料夾（11 檔：刪 3、加 4 → 12 檔）**

- 重寫：`SDK/RimLLMProvider.cs`（靜態入口）
- 刪除：`SDK/IRimLLM.cs`、`SDK/LLMRequest.cs`、`SDK/RimLLMAiOptions.cs`
- 新增：`SDK/RimLLMChatOptions.cs`、`SDK/RimLLMChatClient.cs`、`SDK/RimLLMEmbeddingClient.cs`、`SDK/RimLLMClientExtensions.cs`
- 不動：`SDK/IRimLLMSettings.cs`、`SDK/IChatClientFactories.cs`、`SDK/ProviderIds.cs`、`SDK/LLMProviderCapabilities.cs`、`SDK/LLMError.cs`、`SDK/TestResult.cs`、`SDK/RimLLMException.cs`

**修改：**

- `Manager/RimLLMManager.cs`（移除 `IRimLLM` 實作、新增 `CreateChatClient`／`CreateEmbeddingGenerator`、轉譯層）
- `Manager/RimLLMChatClientExecutor.cs`（`LLMRequest` → 轉譯請求物件、options 合併）
- `Manager/RimLLMEmbeddingService.cs`（供 `RimLLMEmbeddingClient` 包裝；行為不變）
- `Mod/RimLLMFrameworkMod.cs`（若引用 `IRimLLM`／`RimLLMProvider.Instance` 則同步）
- `README.md`（開發者段落全部改寫為 `IChatClient` 範例、技術架構段同步）
- `About/About.xml`（modVersion：`{年,月,日,當天第幾次修改}`）

**測試：**

- `RimLLM Framework.Tests/FrameworkTests.cs`
- `RimLLM Framework.Tests/RimLLMChatClientExecutorTests.cs`
- `RimLLM Framework.Tests/ProviderSdkIntegrationTests.cs`
- 新增：`RimLLM Framework.Tests/RimLLMChatClientTests.cs`、`RimLLMEmbeddingClientTests.cs`

---

## 9. 風險與緩解

| 風險 | 影響 | 緩解 |
|---|---|---|
| 使用者 `ChatOptions` 與框架預設合併衝突 | 參數靜默覆寫 | 「使用者優先、框架補缺」合併規則 + 單元測試 |
| `AdditionalProperties` 不透傳（OpenRouter `models`／`max_thinking_tokens`） | 自訂欄位遺失 | spike §7.1 先行；不可行則維持 `RawRepresentationFactory` 逃生門 |
| 串流 restart 無標準 MEAI 表達 | 呼叫端殘留前段顯示 | `RimLLMChatOptions.OnStreamRestart` callback 維持 |
| 測試重寫量大（40+ 處） | 覆蓋缺口 | 逐批遷移（executor → manager → 整合），對照 §3 行為表逐一驗證 |
| net472 編譯問題（`IAsyncEnumerable` alias） | 建置失敗 | 沿用現有 `Microsoft.Bcl.AsyncInterfaces` alias 機制；測試專案同步驗證 |

---

## 10. 驗收標準

1. `dotnet build "Source/RimLLM Framework.slnx"` 通過。
2. `dotnet test "Source/RimLLM Framework.Tests/RimLLM Framework.Tests.csproj"` 全數通過。
3. 程式庫不再存在 `IRimLLM`、`LLMRequest`、`LLMReasoningEffort`、`RimLLMAiOptions`、`RimLLMProvider.Instance`。
4. 第三方唯一入口是 `RimLLMProvider` 靜態方法 + `IChatClient`／`IEmbeddingGenerator`。
5. §3 對照表逐項有對應測試。
6. README「開發者呼叫說明」與「技術架構」段與實際實作一致；About.xml modVersion 已更新。

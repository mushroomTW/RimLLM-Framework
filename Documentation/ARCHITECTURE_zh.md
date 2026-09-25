# RimLLM Framework —— 架構、安全性與測試

[← 回到 README](../README_zh.md) · [English](ARCHITECTURE.md)

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

* **所有供應商共用一套 SDK。** 內建供應商全都走 `OpenAI` SDK `2.13.0` 與 Microsoft.Extensions.AI `10.10.0` 的 `IChatClient`，沒有直接打 HTTP 的對話路徑，也沒有第二套 SDK。Gemini 走 Google 官方的 OpenAI 相容端點。共用層只依賴 `IChatClient`、`LLMProviderCapabilities` 與 `ILLMProvider`。
* **維持 `net472`。** SDK 的相依 DLL 隨 Mod 出貨，`ProviderSdkIntegrationTests` 會逐一載入，缺少間接相依的組件時在建置階段就失敗，而不是在遊戲裡。
* **`Microsoft.Extensions.AI.Abstractions` 出貨的是 `netstandard2.0` 版本。** NuGet 原本會選的 `net462` 版本參考了 `System.ComponentModel.DataAnnotations`，RimWorld 的 Mono 沒有這個組件，導致遊戲內 `AIFunctionFactory.Create` 擲出 `TypeLoadException`。`ShippedAbstractionsHasNoDataAnnotationsDependency` 負責守住這個選擇。
* **結構化輸出的 schema** 由 `System.Text.Json` 的 `JsonSchemaExporter` 產生，再由 `RimLLMSchemaBuilder` 正規化成每家供應商都接受的子集：`$ref` 展開成內嵌（循環在 CLR 型別層級截斷）、巢狀最多 5 層（OpenAI strict 模式的上限；總共 100 個屬性的上限尚未檢查）、所有成員都列入 `required`，選填性以 `["type","null"]` 聯集表達，並保留 `[Description]`。
* **結構化輸出型別的規則**：schema 產生與反序列化共用同一份 System.Text.Json 契約，因此不要在這些型別上掛自訂 `JsonConverter`。從 Newtonsoft 遷移的 Mod 必須把 `[JsonProperty("x")]` 改成 `[JsonPropertyName("x")]`，並改用 System.Text.Json 的 `[JsonIgnore]`；舊屬性會被無聲忽略。

---

## 🔐 安全性說明

為避免誤解，以下誠實說明每項安全機制實際防護的範圍：

* **API 金鑰加密使用 OS 每使用者保護金鑰。** 新資料使用 AES-256 與隨機儲存金鑰，該金鑰由目前使用者的 OS 保護機制包裝。舊版 `v1`／`v2` 密文仍以裝置衍生素材解密，但只用於遷移；新資料不再從原始碼固定種子或 `deviceUniqueIdentifier` 衍生儲存金鑰。這能防止其他使用者或其他機器直接解密複製的設定，但**無法**防禦以相同使用者身分或在同一 RimWorld 行程內執行的程式碼（包含其他 Mod），因為框架必須使用金鑰才能提供請求服務。
* **`modId` 是歸屬標籤，不是身份驗證。** 每 Mod 節流仍用來維持合作型 Mod 之間的公平性；另外加上「每 Mod 視窗上限十倍」的共享安全上限，每個實際供應商呼叫（包含工具迴圈續輪）都會計入，避免輪換標籤建立無限供應商請求。同一個 RimWorld 行程內的 Mod 仍沒有隔離，請勿把 SDK 當成惡意 Mod 沙盒。
* **金鑰不會進入 URL 或日誌。** 所有供應商都以 HTTP Header 傳遞金鑰；日誌輸出一律經過 `SanitizeForLog` 並截斷長度，診斷匯出中的裝置識別碼也會遮罩。
* **重新整理模型清單時也會連到 models.dev。** 除了供應商本身，重新整理時還會下載公開的 `https://models.dev/api.json` 來補齊上下文上限。這是一般的 GET 請求，不帶 API 金鑰或任何玩家資料；下載失敗時重新整理照常成功，只記一筆警告。
* **供應商輸出視為不受信任的 UI 輸入。** ChatTest 只保留框架自行產生的灰色思考色彩標籤；供應商回應中的 raw HTML 標籤會以文字顯示，再交給 Markdown/Unity IMGUI。

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

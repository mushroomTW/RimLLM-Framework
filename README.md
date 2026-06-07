# RimLLM Framework

`RimLLM Framework` 是一個 RimWorld 模組大型語言模型 (LLM) 呼叫介面與底層核心的基礎框架。它為其他 RimWorld AI 模組提供穩健、安全、高效且開箱即用的 SDK 支援，免去模組開發者重複造輪子的煩惱。

---

## 💻 開發者呼叫說明 (SDK Usage)

### 1. 引用命名空間

請在您的 RimWorld 模組 C# 專案中引用以下命名空間即可：

```csharp
using RimLLM_Framework.SDK;
```

### 2. 註冊您的客戶端 Mod

在您的 Mod 進入點（建構子或 `StaticConstructorOnStartup`）中進行安全註冊：

```csharp
public class MyAIMod : Verse.Mod
{
    public MyAIMod(ModContentPack content) : base(content)
    {
        // 註冊您的 ModId，這會與您當前的 Assembly 綁定以防冒用
        RimLLMProvider.RegisterClient("myai.mod");
    }
}
```

> [!IMPORTANT]
> 為了防範安全漏洞，自 2026.06.05 版本起已**移除了「自動補註冊」機制**。所有呼叫端 Mod 必須在進行任何 API 呼叫前，明確呼叫一次 `RimLLMProvider.RegisterClient` 進行安全性註冊綁定，否則 API 呼叫將會因安全性驗證失敗而拋出異常。

### 3. 文字生成 (Text Generation)

使用 `RimLLMProvider.Instance` 來獲取 LLM 服務，並傳入 `LLMRequest`：

```csharp
public async void AskSomething()
{
    try
    {
        var request = new LLMRequest
        {
            ModId = "myai.mod", // 必須與註冊的 ModId 吻合
            Prompt = "請以 RimWorld 中的說書人蘭迪的口吻，跟玩家打聲招呼。",
            SystemPrompt = "你是一位冷酷、隨機且不可預測的說書人。",
            Temperature = 0.7f,
            MaxTokens = 150
        };

        // 呼叫 GenerateAsync 獲取文字回應 (底層會自動執行故障轉移)
        string response = await RimLLMProvider.Instance.GenerateAsync(request);
        Log.Message($"[RandySays] {response}");
    }
    catch (RimLLMException ex)
    {
        Log.Error($"[MyAIMod] 生成失敗，錯誤碼: {ex.Error}, 錯誤訊息: {ex.Message}");
    }
}
```

### 4. 結構化輸出 (Structured Output)

定義您的 C# 資料格式類別，並調用 `GenerateObjectAsync<T>`：

```csharp
// 1. 定義預期輸出的資料結構
public class PawnIncidentDecision
{
    public string EventType; // "Good" 或 "Bad"
    public string IncidentDefName; // 例如 "RaidEnemy"
    public float Probability;
    public string RandyReasoning; // 說書人的心路歷程
}

public async void MakeIncidentDecision()
{
    var request = new LLMRequest
    {
        ModId = "myai.mod",
        Prompt = "分析當前殖民地的狀況，決定下一個發生的事件類型與 DefName。",
        SystemPrompt = "你是一位專注於製造戲劇性衝突的決策大腦。"
    };

    try
    {
        // 呼叫後底層會自動注入 Schema、發送、容錯修復並反序列化成您的物件
        PawnIncidentDecision decision = await RimLLMProvider.Instance.GenerateObjectAsync<PawnIncidentDecision>(request);
        
        Log.Message($" Randy 決定事件: {decision.IncidentDefName} ({decision.RandyReasoning})");
    }
    catch (RimLLMException ex)
    {1
        Log.Error($"結構化決策生成失敗: {ex.Message}");
    }
}
```

### 5. 串流生成 (Streaming)

若需要像是打字機一般即時渲染模型回傳的文字片段，本框架提供兩種使用方式：

#### 📌 方式 A：統一使用 `GenerateAsync`（推薦：支援動態切換與直接取得完整回覆）

您可以在 `LLMRequest` 中直接設定 `EnableStreaming = true` 並提供 `OnChunkReceived` 回呼。如此一來，您可以用單一的 `await GenerateAsync` 來同時獲得**即時 UI 渲染**與**最終完整字串**，免去另外編寫異步 Task 的麻煩：

```csharp
public async void StreamResponseUnified()
{
    try
    {
        var request = new LLMRequest
        {
            ModId = "myai.mod",
            Prompt = "寫一首關於環世界中被松鼠襲擊的悲壯詩歌。",
            EnableStreaming = true,
            OnChunkReceived = (chunk) => 
            {
                // 此處程式碼保證在 Unity 主線程中執行，可安全更新遊戲 UI
                MyGameUI.AppendText(chunk);
            }
        };

        // 呼叫 GenerateAsync 將會以串流執行，並在完成後回傳完整的字串
        string fullResponse = await RimLLMProvider.Instance.GenerateAsync(request);
        Log.Message($"[Completed] {fullResponse}");
    }
    catch (RimLLMException ex)
    {
        Log.Error($"串流生成失敗: {ex.Message}");
    }
}
```

#### 📌 方式 B：使用 `StreamAsync` 專用方法（傳統做法）

```csharp
public void StreamResponse()
{
    var request = new LLMRequest
    {
        ModId = "myai.mod",
        Prompt = "寫一首關於環世界中被松鼠襲擊的悲壯詩歌。"
    };

    // 呼叫 StreamAsync，傳入收到 chunk 時的回呼函式
    RimLLMProvider.Instance.StreamAsync(request, (chunk) => 
    {
        // 此處程式碼保證在 Unity 主線程中執行，可安全更新遊戲 UI
        MyGameUI.AppendText(chunk);
    }).ContinueWith(task => 
    {
        if (task.IsFaulted)
        {
            Log.Error("串流傳輸中發生異常。");
        }
    });
}
```

### 6. 上下文快取節省 Token (Context Caching)

若您的 Mod 擁有非常龐大的 `SystemPrompt`（例如包含遊戲世界狀態、詳細的角色屬性列表或 XML Schema 等），且需要高頻率呼叫 API，您可以啟用 **上下文快取 (Context Caching)**。

這會自動利用 API 供應商（如 Google Gemini 或 Anthropic Claude）的快取機制，避免每次呼叫都重新計算 System Prompt 的 Token，進而大幅降低 Token 費用與響應延遲：

```csharp
public async void CallWithCaching()
{
    var request = new LLMRequest
    {
        ModId = "myai.mod",
        Prompt = "分析此殖民地成員的心理健康狀態。",
        
        // 設定龐大的系統提示詞
        SystemPrompt = "你是一個 RimWorld 心理分析大師... (此處可能有數千字的角色設定與遊戲資料)",
        
        // 啟用快取節省 Token 功能
        EnableContextCaching = true 
    };

    string response = await RimLLMProvider.Instance.GenerateAsync(request);
    Log.Message(response);
}
```

> [!NOTE]
>
> * **Gemini** 預設會將系統提示詞以 `cachedContents` 方式快取 5 分鐘（TTL 300s），後續相同 `SystemPrompt` 的請求會自動對齊該快取。
> * **Anthropic (Claude)** 則會套用 `ephemeral` 快取標記，對重複輸入的 Prompt 提供高達 90% 的費用折扣。

---

## 📖 主要功能與特色

1. **多供應商支援 (Multi-Provider Support)**
   * 原生支援 Google **Gemini**、**OpenAI**、**DeepSeek**、**Groq**、**Anthropic (Claude)**、**OpenRouter**、**Kimi**、**MiniMax** 與 **Qwen**。
   * 支援 **OpenAI Compatible API**，可自訂配置任何本地或第三方相容介面（如 LM Studio、Ollama、LocalAI、vLLM 等），預設 Endpoint 為 `http://localhost:1234/v1` 且支援 API 金鑰。
   * 針對 **Kimi**、**MiniMax**、**Qwen** 提供「使用中國專用端點 (預設關閉)」一鍵切換，保證優質連線。
2. **故障轉移與雙重備用機制 (Model Fallback)**
   * **客戶端 Fallback 鏈**：支援配置由「主模型」與多個「精確備用模型」組成的輪詢鏈。當前模型遇到請求超時、限流 (Rate Limit 429) 或斷線時，底層自動無縫降級切換。移除了先前不穩定的純供應商無模型 Fallback，當玩家在 UI 中切換供應商時，系統會自動在模型列表中預設選取第一個可用模型。
   * **OpenRouter 服務端自動回退 (openrouter/auto)**：支援在 Fallback 鏈中使用 OpenRouter 官方的 `openrouter/auto` 模型，由 OpenRouter 服務端在多個推薦模型間自動執行備用降級，提供更簡便的模型回退體驗。
3. **AES-256 安全加密與身分安全加固**
   * 使用 AES-256 對稱加密安全保存敏感的 API 金鑰 (API Keys)，杜絕任何純文字儲存外洩的安全隱患。
   * **安全驗證加固**：移除自動補註冊機制，防範假冒 ModId 的安全漏洞；對 async 調用層進行同步外殼與 `NoInlining` 封裝，保障調用端身份識別安全。
4. **精緻的可滾動分欄 GUI**
   * 直覺的 Flow Grid 標籤展示可用模型清單，具備高亮選取與完整模型 Tooltip 提示。
5. **獨立偵錯 (Debug) 分頁與詳細日誌控制**
   * 新增獨立的 **Debug** 設定分頁。提供「詳細日誌 (Detailed Logging)」核取方塊，可自由開啟或關閉本模組的日誌輸出，便於 Mod 開發者與玩家進行排錯。
6. **一鍵連線測試 (Connection Test)**
   * 提供即時的一鍵連線檢測，量測 Latency 延遲並檢驗 API 與模型有效性。在基底類別中高度整合統一實作。
7. **線程安全與主線程 Scribe 調度**
   * 遊戲運行中所有設定字典皆具備同步鎖機制防範多線程並行讀寫衝突。
   * 所有日誌寫入 `RecordLog` 中的 Scribe 存檔均以 `RimLLMDispatcher` 調度回 Unity 主線程執行，並加入 15 秒寫入節流 (Throttle)，避免背景存檔導致遊戲崩潰或 TPS 抖動。
8. **推理性思考模型與思維鏈高亮 (Reasoning Models & think Tagging)**
   * 原生支援 **DeepSeek-R1**、**Gemini 2.0/2.5 Thinking**、**Claude 3.7** 等推理性思考模型。
   * 底層自動擷取 API 返回的思維鏈（如 OpenAI 協定的 `reasoning_content`、Gemini 的 `thought` 欄位、Anthropic 的 `thinking` 區塊），並統一以 XML 標記 `<think>...</think>` 封裝回傳。
   * GUI 聊天測試頁面會自動解析該標記，將其渲染為精緻的灰色斜體思考過程；呼叫端 Mod 亦能極易使用正則表達式剝離或保留思維鏈，確保高相容性。
   * **智慧思考強度控制 (Reasoning Effort)**：預設思考強度改為「自動 / 預設 (Auto)」。在此模式下，各大供應商能運行其原生的適應性或動態思考配置（如 Gemini 的 `thinkingBudget = -1`，Anthropic 的 `"type": "adaptive"`，OpenAI 的動態 `reasoning_effort` 控制等），並支援在選單中一鍵「關閉 (Disabled)」或手動調整思考強度（低/中/高）。
9. **智慧上下文快取與 Prompt Caching (Context Caching)**
   * 原生支援 **Gemini Context Caching** 與 **Anthropic Prompt Caching (Ephemeral)**。開發者只需在 `LLMRequest` 中設定 `EnableContextCaching = true`，底層即會自動將長系統提示詞 (System Prompt) 提交給 API 服務商進行快取，顯著降低高頻重複請求的輸入 Token 費用與延遲。

---

## 🛠️ 技術架構與細節

### 1. 統一介面與調度核心 (`IRimLLM` 與 `RimLLMManager`)

* 提供開發者面向介面的設計，呼叫端僅需對接 `IRimLLM` 介面，完全不需關心底層是由哪個供應商、哪個模型進行生成，全部交由 `RimLLMManager` 進行動態調度與備用輪詢。

### 2. 呼叫端來源身分驗證 (`ClientRegistry`)

* 為了防止遊戲內惡意模組假冒其他合法模組的 ID 竊取 API 金鑰使用權，框架引入了呼叫端組件校驗。
* **已移除自動補註冊機制**：所有呼叫端 Mod 必須手動呼叫 `RegisterClient`。在 API 調用時，底層會檢索當前呼叫端組件的 `Assembly` 並進行強一致性校驗，若該 `ModId` 未經註冊，或註冊的 Assembly 與實際呼叫者不符，將直接予以阻斷，防止越權使用 API 金鑰。
* **async 調用端堆疊保護**：為防止 C# 異步狀態機將調用端組件編譯為 `mscorlib` 造成安全檢驗誤判，本框架對入口層（如 `GenerateObjectAsync` 與 `StreamAsync`）進行了同步外殼包裝，並加入 `[MethodImplOptions.NoInlining]` 屬性防範 JIT 內聯。

### 3. Unity 主線程派遣器 (`RimLLMDispatcher`)

* 網路請求通常是在背景線程（Thread Pool）中異步執行的。然而，Unity 的大部分 API 以及 RimWorld 的邏輯並非線程安全，在背景線程中直接呼叫這些 API 會導致遊戲崩潰或 TPS 抖動。
* `RimLLMDispatcher` 作為一個 MonoBehaviour 單例，利用安全佇列（ConcurrentQueue）收集背景線程發送回來的 Callback，並在 Unity 每幀的 `Update` 週期中將這些 Callback 安全地分發回主線程執行。

### 4. 容錯結構化輸出 (Structured Output & JSON Repair)

* 許多時候開發者需要模型回傳特定的 JSON 格式。
* 框架會自動在 SystemPrompt 中附加基於泛型型別轉換的 JSON Schema 規則指示。
* 由於模型生成的不確定性，回傳的 JSON 可能帶有 Markdown 標記（如 ` ```json `）或是括號未閉合、多餘逗號等瑕疵。框架內置了 `RepairJson` 容錯模組，能自動修復並透過極限正則提取 JSON 區塊，再反序列化為 C# 目標物件，大幅提升結構化輸出的成功率。

---

## 🧪 單元測試與驗證

本專案附帶完整的單元測試套件 `RimLLM Framework.Tests`，覆蓋了 AES 加解密、來源安全驗證、模型 Fallback 機制等核心功能。

您可以在專案根目錄下使用 `dotnet-cli` 執行建置與測試驗證：

```bash
# 還原並重新建置專案
dotnet build

# 執行所有 NUnit 單元測試
dotnet test
```

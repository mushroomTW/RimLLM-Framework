# RimLLM Framework

`RimLLM Framework` 是一個統一 RimWorld 模組大型語言模型 (LLM) 呼叫介面與底層核心的基礎框架。它為其他 RimWorld AI 模組提供穩健、安全、高效且開箱即用的 SDK 支援，免去模組開發者重複造輪子的煩惱。

---

## 💻 開發者呼叫說明 (SDK Usage)

### 1. 引用命名空間

請在您的 RimWorld 模組 C# 專案中引用以下命名空間：

```csharp
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
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
    {
        Log.Error($"結構化決策生成失敗: {ex.Message}");
    }
}
```

### 5. 串流生成 (Streaming)

若需要像是打字機一般即時渲染模型回傳的文字片段，使用 `StreamAsync` 並結合 Action Callback。
**注意：Callback 會自動由 `RimLLMDispatcher` 分發回 Unity 主線程執行，您可以放心地直接在 Callback 中更新 UI 或操作 Unity API！**

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

---

## 📖 主要功能與特色

1. **多供應商支援 (Multi-Provider Support)**
   * 原生支援 Google **Gemini** 與 **OpenAI** 雲端 API。
   * 支援 **OpenAI Compatible API**，可配置任何本地或第三方 LLM 接口（如 LM Studio、Ollama、LocalAI、vLLM 等）。
2. **故障轉移與輪詢備用機制 (Model Fallback)**
   * 支援配置「主模型」與多個「備用模型」鏈。當主模型請求超時、受限 (Rate Limit) 或斷線時，底層將自動且無縫地進行輪詢切換，確保遊戲體驗不中斷。
3. **AES-256 安全加密**
   * 使用 AES-256 對稱加密保存玩家敏感的 API 金鑰 (API Keys)，杜絕金鑰以純文字檔案外洩的安全隱患。
4. **精緻的模型快取管理 GUI**
   * 直覺的 Flow Grid 標籤網格展示，支援滑鼠懸浮高亮與模型名稱完整的 Tooltip 氣泡提示。
   * 自動進行 API 金鑰輸入檢測，若未填寫則給予防呆紅色警示。
5. **一鍵連線測試 (Connection Test)**
   * 在 Mod 設定介面中提供一鍵測試功能，即時量測連線延遲 (Latency) 並檢驗 API 與模型有效性。

---

## 🛠️ 技術架構與細節

### 1. 統一介面與調度核心 (`IRimLLM` 與 `RimLLMManager`)

* 提供開發者面向介面的設計，呼叫端僅需對接 `IRimLLM` 介面，完全不需關心底層是由哪個供應商、哪個模型進行生成，全部交由 `RimLLMManager` 進行動態調度與備用輪詢。

### 2. 呼叫端來源身分驗證 (`ClientRegistry`)

* 為了防止遊戲內惡意模組假冒其他合法模組的 ID 竊取 API 金鑰使用權，框架引入了呼叫端組件校驗。
* 在註冊/調用時，底層會自動檢索呼叫端的 `Assembly`，將其與 `ModId` 進行安全鎖定繫結。若有其他組件企圖冒用該 `ModId`，框架將直接予以阻斷。

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

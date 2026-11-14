using System;
using System.Collections.Generic;

namespace RimLLM_Framework.Providers;

/// <summary>
/// 一次重新整理模型清單的結果：模型名稱清單，以及 API 有回報時的上下文上限。
/// </summary>
internal sealed class ModelCatalog
{
    public ModelCatalog(List<string> models, Dictionary<string, int> contextWindows, Dictionary<string, bool> visionSupport = null)
    {
        Models = models ?? [];
        ContextWindows = contextWindows ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        VisionSupport = visionSupport ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>模型名稱 → 是否支援圖片輸入。只含 API 或 models.dev 有明確資料的模型。</summary>
    public Dictionary<string, bool> VisionSupport { get; }

    public List<string> Models { get; }

    /// <summary>模型名稱 → 上下文上限（token 數）。只含 API 有回報的模型。</summary>
    public Dictionary<string, int> ContextWindows { get; }
}

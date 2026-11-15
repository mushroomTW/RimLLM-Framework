// RimLLM Framework — GlobalSuppressions.cs
// 集中抑制 S101（類型命名應使用 PascalCase）。
// RimLLM 是品牌名稱，重新命名為 RimLlm* 會直接破壞公開 API 與下游 Mod 相容性，
// 因此整個專案關閉此規則，而非逐檔標註。

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Naming", "S101:Types should be named in PascalCase", Justification = "RimLLM 是品牌名稱，保留 RimLLM 拼寫以避免破壞公開 API 與下游 Mod 相容性。")]

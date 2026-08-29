using System.Reflection;
using System.Runtime.CompilerServices;

// 專案的 GenerateAssemblyInfo 為 false，因此 InternalsVisibleTo 與 AssemblyVersion 需在此明確宣告。
// 供測試專案驗證不屬於公開 SDK 契約的內部接縫（例如預算對話框的等待邏輯）。
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: InternalsVisibleTo("RimLLM Framework.Tests")]

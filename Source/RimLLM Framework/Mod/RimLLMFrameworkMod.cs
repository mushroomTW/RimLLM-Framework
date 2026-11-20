using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// RimLLM Framework Mod 本體進入點。
/// 初始化 SDK、掛載 Dispatcher 並委託設定 GUI 的渲染。
/// 類別名稱保留 RimLLM 品牌名，不重新命名為 RimLlm，以避免破壞品牌識別與下游 Mod 相容性。
/// </summary>
public class RimLLMFrameworkMod : Verse.Mod
{
    /// <summary>
    /// 全域設定檔實例。
    /// </summary>
    internal static RimLLMFrameworkSettings Settings { get; private set; } = null!; // 由 Mod 建構子（遊戲載入 Mod 時）指派，之後才會被存取。

    public RimLLMFrameworkMod(ModContentPack content) : base(content)
    {
        // 1. 載入並儲存 Settings 實體
        Settings = GetSettings<RimLLMFrameworkSettings>();

        // 2. 註冊 SDK 服務管理器到 Provider 入口 (依賴注入 Settings)
        //    歸屬快照以 foreach 收集而非 LINQ lambda：lambda 會產生型別為 Func<ModMetaData, …> 的快取欄位，
        //    反射載入該欄位會連帶要求 Steamworks 組件，讓相容層的型別掃描測試失敗。
        var activeMods = new List<(string PackageId, string Name, string Url)>();
        foreach (ModMetaData mod in ModsConfig.ActiveModsInLoadOrder)
        {
            activeMods.Add((mod.PackageIdPlayerFacing, mod.Name, mod.Url));
        }
        RimLLMProvider.Initialize(new RimLLMManager(Settings)
        {
            AttributionResolver = RimLLMAppAttribution.CreateResolver(activeMods)
        });

        // 3. 於主線程建立 Unity 主線程派遣器 (RimLLMDispatcher)。
        //    背景執行緒不得建立 Unity GameObject，因此建立時機必須固定在此。
        RimLLMDispatcher.EnsureInitialized();

        // 4. 初始化 UI 狀態
        RimLLMSettingsUI.Initialize(Settings);

        // 5. 註冊關閉時的遙測強制寫入。
        //    用量統計採 15 秒節流，若沒有這步，session 最後一段用量與費用會遺失。
        //    RimWorld 沒有保證的關閉回呼，因此兩個掛勾互補註冊。
        RegisterShutdownFlush();

        // 6. 背景下載 models.dev 目錄，讓費用估算與模型分級採用最新費率（失敗時退回內建費率表）。
        _ = ModelsDevCatalog.GetAllAsync(Settings.ApiTimeout);

        Log.Message("[RimLLM] RimLLM Framework loaded.");
    }

    private static void RegisterShutdownFlush()
    {
        Application.quitting += FlushTelemetryQuietly;
        AppDomain.CurrentDomain.ProcessExit += (sender, args) => FlushTelemetryQuietly();
    }

    private static void FlushTelemetryQuietly()
    {
        try
        {
            Settings?.FlushTelemetryIfDirty();
        }
        catch (Exception ex)
        {
            // 關閉期間 GenFilePaths 等 Verse 服務可能已不可用，不得讓例外中斷關機流程。
            // RimLLMLog 本身會在 Verse.Log 失敗時退回主控台輸出，因此這裡不需要再包一層 try/catch。
            RimLLMLog.Warning($"[RimLLM] Failed to write telemetry on shutdown: {ex.Message}");
        }
    }

    public override string SettingsCategory()
    {
        return "RimLLM Framework";
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        base.DoSettingsWindowContents(inRect);

        // 委託給獨立的 UI 渲染類別
        RimLLMSettingsUI.DoSettingsWindowContents(inRect);
    }
}

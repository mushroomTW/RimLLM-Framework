using System;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// RimLLM Framework 的 Mod 進入點。
/// 類別名稱保留 RimLLM 品牌名而不改為 RimLlm，以避免破壞下游 Mod 相容性。
/// </summary>
public class RimLLMFrameworkMod : Verse.Mod
{
    internal static RimLLMFrameworkSettings Settings { get; private set; } = null!;

    public RimLLMFrameworkMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<RimLLMFrameworkSettings>();

        // 以 foreach 收集而非 LINQ lambda：lambda 會產生 Func<ModMetaData, …> 快取欄位，反射載入時連帶要求 Steamworks 組件，使相容層型別掃描測試失敗。
        var activeMods = new List<(string PackageId, string Name, string Url)>();
        foreach (ModMetaData mod in ModsConfig.ActiveModsInLoadOrder)
        {
            activeMods.Add((mod.PackageIdPlayerFacing, mod.Name, mod.Url));
        }
        RimLLMProvider.Initialize(new RimLLMManager(Settings)
        {
            AttributionResolver = RimLLMAppAttribution.CreateResolver(activeMods)
        });

        // 須於主線程建立：背景執行緒不得建立 Unity GameObject。
        RimLLMDispatcher.EnsureInitialized();

        RimLLMSettingsUI.Initialize(Settings);

        // 用量統計 15 秒節流，須在關閉時強制寫入；RimWorld 無保證的關閉回呼，故兩個掛勾互補。
        RegisterShutdownFlush();

        _ = ModelsDevCatalog.GetAllAsync(Settings.ApiTimeout);

        RimLLMLog.Message("[RimLLM] RimLLM Framework loaded.");
    }

    private static void RegisterShutdownFlush()
    {
        Application.quitting += FlushTelemetryQuietly;
        AppDomain.CurrentDomain.ProcessExit += (sender, args) => FlushTelemetryQuietly();
    }

    private static void FlushTelemetryQuietly()
    {
        Compat.LocalOpenAIEndpoint.Stop();
        try
        {
            Settings?.FlushTelemetryIfDirty();
        }
        catch (Exception ex)
        {
            // 關閉期間 GenFilePaths 等 Verse 服務可能已不可用，不得讓例外中斷關機流程。
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

        RimLLMSettingsUI.DoSettingsWindowContents(inRect);
    }

    public override void WriteSettings()
    {
        base.WriteSettings();
        ChatTestDrawer.CancelActiveChatRequest();
    }
}

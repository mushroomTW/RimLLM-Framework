using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimLLM_Framework.Compat;

/// <summary>
/// 簡中自動化 One（RimTranslation）的接管設定：不攔截它的任何方法，只把它的 API 設定指向框架的本機端點。
/// </summary>
/// <remarks>
/// <para>
/// One 的翻譯、評審、連線測試、能力測試全都是「對 Base URL 發 OpenAI 相容請求」，
/// 所以只要 Base URL 指向 <see cref="LocalOpenAIEndpoint"/>，所有功能自然走框架，
/// 不需要像過去那樣逐一攔截它的傳輸層、金鑰檢查與探測流程。
/// </para>
/// <para>
/// 它的類別多為 <c>internal</c>，無法在編譯期引用，因此寫入設定只靠反射呼叫它自己的
/// <c>ApiConfigurationStore.Save</c>（由它處理格式、檔案與金鑰檔），型別與方法以名稱解析。
/// 金鑰不寫入（本機端點不驗證金鑰，真實金鑰不離開框架）。
/// </para>
/// </remarks>
internal static class ScAutoCompatPatch
{
    private const string Namespace = "RimTranslation.Api.";

    private const string DisplayName = "Simplified Chinese Automation: One";

    /// <summary>啟動時呼叫：登錄端點處理器，接管已開啟的話一併寫入設定。不需要 Harmony。</summary>
    public static void Apply(Harmony harmony)
    {
        _ = harmony;
        CompatSeedUtil.RegisterEndpoint(ScAutoCompatTarget.PackageId, OpenAICompatClient.DefaultMaxOutputTokens);
        OnTakeoverToggled(Mod.RimLLMFrameworkMod.Settings?.IsCompatTakeoverEnabled(ScAutoCompatTarget.PackageId) == true);
    }

    /// <summary>
    /// 玩家切換接管開關或專用模型時呼叫：開啟時把端點位址寫進它的設定，關閉時移除框架寫入的位址。
    /// </summary>
    public static void OnTakeoverToggled(bool enabled)
    {
        if (enabled)
        {
            SeedNativeConfig();
        }
        else
        {
            RevertNativeConfig();
        }
    }

    /// <summary>
    /// 接管關閉時，若位址是框架寫的，改回它的預設位址（它不接受空位址）。
    /// 框架不寫入金鑰，還原後它會以「未設定金鑰」明確報錯，而不是持續連到已不服務的本機端點。
    /// </summary>
    internal static void RevertNativeConfig()
    {
        try
        {
            Type store = AccessTools.TypeByName(Namespace + "ApiConfigurationStore");
            MethodInfo strictLoad = store == null ? null : AccessTools.Method(store, "Load", Type.EmptyTypes);
            MethodInfo createDefault = store == null ? null : AccessTools.Method(store, "CreateDefault", Type.EmptyTypes);
            MethodInfo save = strictLoad == null ? null : AccessTools.Method(store, "Save", [strictLoad.ReturnType, typeof(string), typeof(bool)]);
            if (save == null || createDefault == null)
            {
                return;
            }

            object config;
            try
            {
                config = strictLoad.Invoke(null, null);
            }
            catch (TargetInvocationException)
            {
                return; // 沒有設定檔：沒有需要還原的東西。
            }

            PropertyInfo baseUrl = config == null ? null : AccessTools.Property(config.GetType(), "BaseUrl");
            if (baseUrl == null || !LocalOpenAIEndpoint.IsOwnBaseUrl(baseUrl.GetValue(config) as string, ScAutoCompatTarget.PackageId))
            {
                return;
            }

            baseUrl.SetValue(config, baseUrl.GetValue(createDefault.Invoke(null, null)));
            save.Invoke(null, [config, string.Empty, false]);
            RefreshNativeSettingsDraft(AccessTools.Method(store, "LoadForEditing"));
            CompatSeedUtil.LogReverted(DisplayName);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnRevertFailed(DisplayName, ex);
        }
    }

    /// <summary>
    /// 把它的設定改成指向本機端點。只改「沒有設定」或「框架自己先前寫的」位址（埠號每次啟動都不同，
    /// 需要更新）；玩家自己填的真實位址一律不動。
    /// </summary>
    internal static void SeedNativeConfig()
    {
        try
        {
            string endpointUrl = LocalOpenAIEndpoint.GetBaseUrl(ScAutoCompatTarget.PackageId);
            if (endpointUrl == null)
            {
                Log.Warning($"[RimLLM] Compat: the local endpoint is not available, so {DisplayName} keeps its own settings.");
                return;
            }

            Type store = AccessTools.TypeByName(Namespace + "ApiConfigurationStore");
            MethodInfo load = store == null ? null : AccessTools.Method(store, "LoadForEditing");
            MethodInfo save = load == null ? null : AccessTools.Method(store, "Save", [load.ReturnType, typeof(string), typeof(bool)]);
            if (save == null)
            {
                Log.Warning("[RimLLM] Compat: ApiConfigurationStore.Save was not found (Simplified Chinese Automation: One may have changed); its settings were not pre-filled.");
                return;
            }

            // LoadForEditing 在沒有設定檔時會回傳帶 OpenAI 預設位址的草稿，不能拿它判斷「玩家設定過」；
            // 改用會擲錯的 Load()：能載入且位址不是框架自己寫的，才代表玩家有自己的設定。
            bool refreshingOwnConfig = false;
            MethodInfo strictLoad = AccessTools.Method(store, "Load", Type.EmptyTypes);
            if (strictLoad != null)
            {
                try
                {
                    object existing = strictLoad.Invoke(null, null);
                    string existingUrl = existing == null ? null : (string)AccessTools.Property(existing.GetType(), "BaseUrl").GetValue(existing);
                    if (!string.IsNullOrWhiteSpace(existingUrl) && !LocalOpenAIEndpoint.IsOwnBaseUrl(existingUrl, ScAutoCompatTarget.PackageId))
                    {
                        CompatSeedUtil.WarnKeptNativeConfig(DisplayName);
                        return;
                    }
                    refreshingOwnConfig = !string.IsNullOrWhiteSpace(existingUrl);
                }
                catch (TargetInvocationException)
                {
                    // 設定檔不存在或無效：視為尚未設定。
                }
            }

            object config = load.Invoke(null, null);
            Type configType = config.GetType();

            AccessTools.Property(configType, "BaseUrl").SetValue(config, endpointUrl);
            // 請求的模型欄位不影響路由，每次都寫入目前的專用模型名，設定頁顯示才會跟著玩家的選擇。
            AccessTools.Property(configType, "Model").SetValue(config, CompatSeedUtil.ModelLabel(ScAutoCompatTarget.PackageId));
            if (!refreshingOwnConfig)
            {
                // 首次寫入才給預設協議；之後只更新埠號與模型名，不覆蓋玩家在設定頁改過的協議。
                AccessTools.Property(configType, "Protocol").SetValue(config, "responses");
            }
            save.Invoke(null, [config, string.Empty, false]);
            RefreshNativeSettingsDraft(load);
        }
        catch (Exception ex)
        {
            CompatSeedUtil.WarnFailed(DisplayName, ex);
        }
    }

    /// <summary>
    /// One 在建構時就把設定檔讀進私有欄位 <c>apiDraft</c> 當作設定頁的草稿，之後不會重讀。
    /// 我們晚於它才寫入檔案，所以要把草稿換成剛寫好的內容，設定頁才看得到。
    /// </summary>
    private static void RefreshNativeSettingsDraft(MethodInfo loadForEditing)
    {
        Type mod = AccessTools.TypeByName("RimTranslation.Core.RimTranslationMod");
        object instance = mod == null ? null : AccessTools.Property(mod, "Instance")?.GetValue(null);
        FieldInfo draft = mod == null ? null : AccessTools.Field(mod, "apiDraft");
        if (instance == null || draft == null)
        {
            return;
        }

        draft.SetValue(instance, loadForEditing.Invoke(null, null));
    }
}

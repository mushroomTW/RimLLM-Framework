using System;
using UnityEngine;
using Verse;
using RimLLM_Framework.SDK;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// RimLLM Framework Mod 本體進入點。
    /// 初始化 SDK、掛載 Dispatcher 並委託設定 GUI 的渲染。
    /// </summary>
    public class RimLLMFrameworkMod : Verse.Mod
    {
        /// <summary>
        /// 全域設定檔實例。
        /// </summary>
        public static RimLLMFrameworkSettings Settings { get; private set; }

        public RimLLMFrameworkMod(ModContentPack content) : base(content)
        {
            // 1. 載入並儲存 Settings 實體
            Settings = GetSettings<RimLLMFrameworkSettings>();

            // 2. 註冊 SDK 服務管理器到 Provider 入口 (依賴注入 Settings)
            RimLLMProvider.Initialize(new RimLLMManager(Settings));

            // 3. 強制觸發 Unity 主線程派遣器 (RimLLMDispatcher) 單例建立
            var dispatcher = RimLLMDispatcher.Instance;

            // 4. 初始化 UI 狀態
            RimLLMSettingsUI.Initialize(Settings);
            
            Log.Message("[RimLLM] RimLLM Framework 載入成功。");
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
}

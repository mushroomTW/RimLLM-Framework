using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager.Services;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class ResetAllSettingsTests
{
    [Test]
    public void ResetAllSettingsToDefaults_ClearsEverySettingIncludingKeys()
    {
        var settings = new RimLLMFrameworkSettings();
        settings.SetApiKey(ProviderIds.Gemini, "chat-key");
        settings.SetProviderEnabled(ProviderIds.Gemini, true);
        settings.SetModelList(ProviderIds.Gemini, new List<string> { "models/gemini-2.5-flash" });
        settings.FallbackChain = new List<string> { "Gemini:models/gemini-2.5-flash" };
        settings.EmbeddingProvider = "Google";
        settings.SetEmbeddingModel("Google", "gemini-embedding-2");
        settings.SetEmbeddingApiKey("Google", "embed-key");
        settings.DecisionProvider = RimLLMDecisionProviderCatalog.Gemini;
        settings.SetDecisionModel(RimLLMDecisionProviderCatalog.Gemini, "gemini-decision-1");
        settings.ApiTimeout = 99f;
        settings.DailyTokenBudgetLimit = 1234;
        int versionBefore = settings.ModelListVersion;

        settings.ResetAllSettingsToDefaults();

        var fresh = new RimLLMFrameworkSettings();
        ClassicAssert.AreEqual("", settings.GetApiKey(ProviderIds.Gemini));
        ClassicAssert.AreEqual(fresh.IsProviderEnabled(ProviderIds.Gemini), settings.IsProviderEnabled(ProviderIds.Gemini));
        ClassicAssert.AreEqual(0, settings.GetModelCount(ProviderIds.Gemini));
        CollectionAssert.IsEmpty(settings.FallbackChain);
        ClassicAssert.AreEqual(RimLLMFrameworkSettings.DisabledProvider, settings.EmbeddingProvider);
        ClassicAssert.AreEqual("", settings.GetEmbeddingModel("Google"));
        ClassicAssert.AreEqual("", settings.GetEmbeddingApiKey("Google"));
        ClassicAssert.AreEqual(RimLLMDecisionProviderCatalog.Disabled, settings.DecisionProvider);
        ClassicAssert.AreEqual("", settings.GetDecisionModel(RimLLMDecisionProviderCatalog.Gemini));
        ClassicAssert.AreEqual(fresh.ApiTimeout, settings.ApiTimeout);
        ClassicAssert.AreEqual(0, settings.DailyTokenBudgetLimit);
        // 模型清單被清空：設定頁的過濾快取必須失效。
        ClassicAssert.Greater(settings.ModelListVersion, versionBefore);
    }
}

using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 首頁「能不能用」狀態判斷、精靈失敗訊息對照，以及「等待與重試」頁的恢復預設。
/// </summary>
[TestFixture]
public class SetupStatusTests
{
    [Test]
    public void EmptyChainIsNotConnected()
    {
        ClassicAssert.AreEqual(SetupState.NotConnected, SetupStatus.Evaluate(new List<string>(), _ => true, out string entry));
        ClassicAssert.IsNull(entry);
        ClassicAssert.AreEqual(SetupState.NotConnected, SetupStatus.Evaluate(null, _ => true, out _));
    }

    [Test]
    public void ChainWithOnlyBlankEntriesIsNotConnected()
    {
        ClassicAssert.AreEqual(SetupState.NotConnected, SetupStatus.Evaluate(new List<string> { "", null }, _ => true, out _));
    }

    [Test]
    public void ChainWithoutUsableProviderIsBroken()
    {
        var chain = new List<string> { "Gemini:gemini-3.5-flash", "OpenAI:gpt-4o-mini" };
        ClassicAssert.AreEqual(SetupState.Broken, SetupStatus.Evaluate(chain, _ => false, out string entry));
        ClassicAssert.IsNull(entry);
    }

    [Test]
    public void FirstUsableEntryMakesItReady()
    {
        var chain = new List<string> { "Gemini:gemini-3.5-flash", "DeepSeek:deepseek-v4-flash", "OpenAI:gpt-4o-mini" };
        SetupState state = SetupStatus.Evaluate(chain, id => id != "Gemini", out string entry);

        ClassicAssert.AreEqual(SetupState.Ready, state);
        ClassicAssert.AreEqual("DeepSeek:deepseek-v4-flash", entry, "首頁要顯示第一個真的會被用到的條目。");
    }

    [Test]
    public void ProviderOnlyEntryIsParsed()
    {
        ClassicAssert.AreEqual(SetupState.Ready, SetupStatus.Evaluate(new List<string> { "Player2" }, id => id == "Player2", out string entry));
        ClassicAssert.AreEqual("Player2", entry);
    }

    [TestCase(LLMError.InvalidKey, "RimLLM_SetupFail_Key")]
    [TestCase(LLMError.RateLimit, "RimLLM_SetupFail_Quota")]
    [TestCase(LLMError.QuotaExceeded, "RimLLM_SetupFail_Quota")]
    [TestCase(LLMError.Timeout, "RimLLM_SetupFail_Offline")]
    [TestCase(LLMError.ProviderOffline, "RimLLM_SetupFail_Offline")]
    [TestCase(LLMError.NetworkError, "RimLLM_SetupFail_Offline")]
    [TestCase(LLMError.ModelNotFound, "RimLLM_SetupFail_Other")]
    [TestCase(LLMError.Unknown, "RimLLM_SetupFail_Other")]
    public void FailureCodesMapToPlainMessages(LLMError error, string expectedKey)
    {
        ClassicAssert.AreEqual(expectedKey, SetupStatus.DescribeFailureKey(error));
    }

    [Test]
    public void ResetRequestSettingsRestoresFreshDefaults()
    {
        var fresh = new RimLLMFrameworkSettings();
        var settings = new RimLLMFrameworkSettings
        {
            ApiTimeout = 99f,
            MaxRetries = 9,
            RetryDelay = 9f,
            MaxConcurrentRequests = 9,
            EnableNativeSchema = false,
            EnableJsonRepair = false,
            DefaultReasoningEffort = Microsoft.Extensions.AI.ReasoningEffort.High
        };
        settings.FallbackChain = new List<string> { "Gemini:keep-me" };

        settings.ResetRequestSettingsToDefaults();

        ClassicAssert.AreEqual(fresh.ApiTimeout, settings.ApiTimeout);
        ClassicAssert.AreEqual(fresh.MaxRetries, settings.MaxRetries);
        ClassicAssert.AreEqual(fresh.RetryDelay, settings.RetryDelay);
        ClassicAssert.AreEqual(fresh.MaxConcurrentRequests, settings.MaxConcurrentRequests);
        ClassicAssert.AreEqual(fresh.EnableNativeSchema, settings.EnableNativeSchema);
        ClassicAssert.AreEqual(fresh.EnableJsonRepair, settings.EnableJsonRepair);
        ClassicAssert.AreEqual(fresh.DefaultReasoningEffort, settings.DefaultReasoningEffort);
        CollectionAssert.AreEqual(new[] { "Gemini:keep-me" }, settings.FallbackChain, "恢復預設不能動到備援鏈。");
    }
}

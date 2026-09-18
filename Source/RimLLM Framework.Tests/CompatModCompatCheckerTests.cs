using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using ModCompatChecker.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// Mod 兼容性檢查器相容層單元測試：型別載入防線、訊息封裝、參數設定、路由與開關。
    /// </summary>
    [TestFixture]
    public class CompatModCompatCheckerTests
    {
        [Test]
        public void ModCompatCheckerTarget_DeclaresStablePackageId()
        {
            var target = new ModCompatCheckerCompatTarget();
            ClassicAssert.AreEqual("modcompatchecker.main", target.ModId);
            ClassicAssert.AreEqual("Mod 兼容性检查器 (Mod Compatibility Checker)", target.DisplayName);
            ClassicAssert.IsFalse(target.IsPatched);
        }

        [Test]
        public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
        {
            var settings = new RimLLMFrameworkSettings();

            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId));
            settings.SetCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId, true);
            ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId));
            settings.SetCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId, false);
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatTarget.PackageId));
        }

        [Test]
        public void Client_CallAPI_ReturnsModelResponseText()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "診斷結果：這是一個 Harmony 補丁衝突"))
                {
                    ModelId = "test-model",
                    Usage = new UsageDetails
                    {
                        InputTokenCount = 100,
                        OutputTokenCount = 50
                    }
                }
            };
            var client = new ModCompatCheckerCompatClient(fake);

            bool cancel = false;
            string result = client.CallAPI("某某衝突分析提示詞", 30, ref cancel);

            ClassicAssert.IsNotNull(result);
            StringAssert.Contains("診斷結果", result);
            StringAssert.Contains("Harmony 補丁衝突", result);
        }

        [Test]
        public void Client_BuildsMessagesCorrectlyAndDisablesReasoning()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
            };
            var client = new ModCompatCheckerCompatClient(fake);

            bool cancel = false;
            client.CallAPI("測試診斷內容", 30, ref cancel);

            ChatMessage[] messages = fake.ReceivedMessages.Single().ToArray();
            ClassicAssert.AreEqual(1, messages.Length);
            ClassicAssert.AreEqual(ChatRole.User, messages[0].Role);
            ClassicAssert.AreEqual("測試診斷內容", messages[0].Text);

            ChatOptions options = fake.ReceivedOptions.Single();
            ClassicAssert.IsInstanceOf<RimLLMChatOptions>(options);
            var rimOptions = (RimLLMChatOptions)options;
            ClassicAssert.IsTrue(rimOptions.DisableReasoning);
            ClassicAssert.AreEqual(0.3f, rimOptions.Temperature);
        }

        [Test]
        public void Prefix_CallAPIWithTimeout_RoutesToRimLLMWhenEnabled()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "RimLLM 接管回覆"))
                {
                    Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 10 }
                }
            };

            ModCompatCheckerCompatClient previousClient = ModCompatCheckerCompatPatch.Client;

            try
            {
                ModCompatCheckerCompatPatch.Client = new ModCompatCheckerCompatClient(fake);

                // 強制快取為接管開啟
                typeof(ModCompatCheckerCompatPatch)
                    .GetField("_cachedTakeOver", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, true);
                typeof(ModCompatCheckerCompatPatch)
                    .GetField("_cachedAt", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, DateTime.UtcNow);

                string result = null;
                bool cancel = false;
                bool runOriginal = ModCompatCheckerCompatPatch.CallAPIWithTimeoutPrefix(
                    "http://example.com", "key", "model", "診斷內容",
                    ModelConfig.ApiProvider.OpenAI, 30, ref cancel, ref result);

                ClassicAssert.IsFalse(runOriginal, "接管開啟時應跳過原生");
                ClassicAssert.IsNotNull(result);
                StringAssert.Contains("RimLLM 接管回覆", result);
            }
            finally
            {
                ModCompatCheckerCompatPatch.Client = previousClient;
            }
        }

        [Test]
        public void Prefix_CallAPIWithTimeout_PassesThroughWhenDisabled()
        {
            ModCompatCheckerCompatClient previousClient = ModCompatCheckerCompatPatch.Client;

            try
            {
                // 強制快取為接管關閉
                typeof(ModCompatCheckerCompatPatch)
                    .GetField("_cachedTakeOver", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, false);
                typeof(ModCompatCheckerCompatPatch)
                    .GetField("_cachedAt", BindingFlags.NonPublic | BindingFlags.Static)
                    .SetValue(null, DateTime.UtcNow);

                string result = null;
                bool cancel = false;
                bool runOriginal = ModCompatCheckerCompatPatch.CallAPIWithTimeoutPrefix(
                    "http://example.com", "key", "model", "診斷內容",
                    ModelConfig.ApiProvider.OpenAI, 30, ref cancel, ref result);

                ClassicAssert.IsTrue(runOriginal, "接管關閉時應放行原生");
                ClassicAssert.IsNull(result);
            }
            finally
            {
                ModCompatCheckerCompatPatch.Client = previousClient;
            }
        }

        [Test]
        public void Postfix_IsAIConfigured_OverridesWhenTakeoverEnabled()
        {
            // 強制快取為接管開啟
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedTakeOver", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, true);
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedAt", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, DateTime.UtcNow);

            bool result = false; // 原生回傳 false（未設 API Key）
            ModCompatCheckerCompatPatch.IsAIConfiguredPostfix(ref result);

            ClassicAssert.IsTrue(result, "接管開啟時應強制為 true");
        }

        [Test]
        public void Postfix_IsAIConfigured_DoesNotOverrideWhenDisabled()
        {
            // 強制快取為接管關閉
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedTakeOver", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, false);
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedAt", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, DateTime.UtcNow);

            bool result = false;
            ModCompatCheckerCompatPatch.IsAIConfiguredPostfix(ref result);

            ClassicAssert.IsFalse(result, "接管關閉時不應覆蓋");
        }

        [Test]
        public void Prefix_CheckBalance_SkipsWhenTakeoverEnabled()
        {
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedTakeOver", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, true);
            typeof(ModCompatCheckerCompatPatch)
                .GetField("_cachedAt", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, DateTime.UtcNow);

            bool runOriginal = ModCompatCheckerCompatPatch.CheckBalancePrefix();
            ClassicAssert.IsFalse(runOriginal, "接管開啟時應跳過餘額檢查");
        }

        /// <summary>
        /// 鎖住相容層的型別載入防線：框架 DLL 裡任何型別（含編譯器生成的 async 狀態機、closure、快取的 lambda
        /// 委派）都不得以 ModCompatChecker 型別當基底類別、介面或欄位型別。
        /// </summary>
        [Test]
        public void FrameworkAssembly_HasNoModCompatCheckerTypesInBaseInterfacesOrFields()
        {
            var offenders = new List<string>();
            foreach (Type type in typeof(ModCompatCheckerCompatPatch).Assembly.GetTypes())
            {
                foreach ((string where, Type dependency) in TypeLoadDependencies(type))
                {
                    if (dependency.Assembly.GetName().Name == "ModCompatChecker")
                    {
                        offenders.Add(type.FullName + " " + where + " -> " + dependency.FullName);
                    }
                }
            }

            ClassicAssert.IsEmpty(offenders,
                "這些型別在基底／介面／欄位層級參考了 ModCompatChecker，ModCompatChecker 未安裝時框架會被 RimWorld 拒載或在 DevMode 啟動時報錯。");
        }

        private static IEnumerable<(string where, Type type)> TypeLoadDependencies(Type type)
        {
            const BindingFlags allFields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            if (type.BaseType != null)
            {
                foreach (Type t in Expand(type.BaseType)) yield return ("base", t);
            }
            foreach (Type iface in type.GetInterfaces())
            {
                foreach (Type t in Expand(iface)) yield return ("interface", t);
            }
            foreach (FieldInfo field in type.GetFields(allFields))
            {
                foreach (Type t in Expand(field.FieldType)) yield return ("field " + field.Name, t);
            }
        }

        private static IEnumerable<Type> Expand(Type type)
        {
            yield return type;
            if (type.HasElementType)
            {
                foreach (Type t in Expand(type.GetElementType())) yield return t;
            }
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    foreach (Type t in Expand(argument)) yield return t;
                }
            }
        }
    }
}

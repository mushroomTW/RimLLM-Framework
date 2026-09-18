using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AutoTranslation.Translators;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// Auto Translation 相容層單元測試：型別載入防線、訊息封裝、參數設定、路由與開關。
    /// </summary>
    [TestFixture]
    public class CompatAutoTranslationTests
    {
        [Test]
        public void AutoTranslationTarget_DeclaresStablePackageId()
        {
            var target = new AutoTranslationCompatTarget();
            ClassicAssert.AreEqual("seohyeon.autotranslation", target.ModId);
            ClassicAssert.AreEqual("Auto Translation", target.DisplayName);
            ClassicAssert.IsFalse(target.IsPatched);
        }

        [Test]
        public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
        {
            var settings = new RimLLMFrameworkSettings();

            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
            settings.SetCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId, true);
            ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
            settings.SetCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId, false);
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(AutoTranslationCompatTarget.PackageId));
        }

        [Test]
        public void Client_GetResponseUnsafe_FormatsValidJsonWithTokens()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "翻譯成功文本"))
                {
                    ModelId = "test-model",
                    Usage = new UsageDetails
                    {
                        InputTokenCount = 12,
                        OutputTokenCount = 8
                    }
                }
            };
            var client = new AutoTranslationCompatClient(fake);

            string json = client.GetResponseUnsafe("原文", "系統提示詞");

            ClassicAssert.IsNotNull(json);
            StringAssert.Contains("\"content\":\"翻譯成功文本\"", json.Replace(" ", string.Empty));
            StringAssert.Contains("\"prompt_tokens\":12", json.Replace(" ", string.Empty));
            StringAssert.Contains("\"completion_tokens\":8", json.Replace(" ", string.Empty));
        }

        [Test]
        public void Client_BuildsMessagesCorrectlyAndDisablesReasoning()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK"))
            };
            var client = new AutoTranslationCompatClient(fake);

            client.GetResponseUnsafe("測試內容", "翻譯指示");

            ChatMessage[] messages = fake.ReceivedMessages.Single().ToArray();
            ClassicAssert.AreEqual(2, messages.Length);
            ClassicAssert.AreEqual(ChatRole.System, messages[0].Role);
            ClassicAssert.AreEqual("翻譯指示", messages[0].Text);
            ClassicAssert.AreEqual(ChatRole.User, messages[1].Role);
            ClassicAssert.AreEqual("測試內容", messages[1].Text);

            ChatOptions options = fake.ReceivedOptions.Single();
            ClassicAssert.IsInstanceOf<RimLLMChatOptions>(options);
            var rimOptions = (RimLLMChatOptions)options;
            ClassicAssert.IsTrue(rimOptions.DisableReasoning);
            ClassicAssert.AreEqual(0.3f, rimOptions.Temperature);
        }

        [Test]
        public void Prefixes_RouteOnlySentinelInstance()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "攔截成功"))
                {
                    Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 }
                }
            };

            AutoTranslationCompatClient previousClient = AutoTranslationCompatPatch.Client;
            object previousSentinel = AutoTranslationCompatPatch.Sentinel;

            try
            {
                AutoTranslationCompatPatch.Client = new AutoTranslationCompatClient(fake);

                var sentinel = new Translator_OpenAICompatible();
                var native = new Translator_OpenAICompatible();
                AutoTranslationCompatPatch.Sentinel = sentinel;

                // 模擬開啟接管
                AutoTranslationCompatPatch.OnTakeoverToggled(true);

                // 原生實例：應放行（回傳 true）且 __result 為空
                string nativeResult = null;
                bool nativeHandled = AutoTranslationCompatPatch.GetResponseUnsafePrefix(native, "text", "prompt", ref nativeResult);
                ClassicAssert.IsTrue(nativeHandled);
                ClassicAssert.IsNull(nativeResult);

                // 哨兵實例：應攔截（回傳 false）且 __result 有值
                string sentinelResult = null;
                bool sentinelHandled = AutoTranslationCompatPatch.GetResponseUnsafePrefix(sentinel, "text", "prompt", ref sentinelResult);
                ClassicAssert.IsFalse(sentinelHandled);
                ClassicAssert.IsNotNull(sentinelResult);
                StringAssert.Contains("攔截成功", sentinelResult);
            }
            finally
            {
                AutoTranslationCompatPatch.Client = previousClient;
                AutoTranslationCompatPatch.Sentinel = previousSentinel;
            }
        }

        [Test]
        public void SyncCurrentTranslator_SwapsSentinelAndNativeCorrectly()
        {
            object previousSentinel = AutoTranslationCompatPatch.Sentinel;
            object previousNative = AutoTranslationCompatPatch.NativeTranslator;
            ITranslator previousCurrent = AutoTranslation.Services.TranslatorManager.CurrentTranslator;
            bool previousReady = AutoTranslation.Services.TranslatorManager.Ready;

            try
            {
                var sentinel = new Translator_OpenAICompatible();
                var native = new Translator_OpenAICompatible();
                AutoTranslationCompatPatch.Sentinel = sentinel;
                AutoTranslationCompatPatch.NativeTranslator = native;
                AutoTranslation.Services.TranslatorManager.CurrentTranslator = native;

                // 開啟接管：應置換為哨兵
                AutoTranslationCompatPatch.OnTakeoverToggled(true);
                ClassicAssert.AreSame(sentinel, AutoTranslation.Services.TranslatorManager.CurrentTranslator);
                ClassicAssert.IsTrue(AutoTranslation.Services.TranslatorManager.Ready);

                // 關閉接管：應還原為原生
                AutoTranslationCompatPatch.OnTakeoverToggled(false);
                ClassicAssert.AreSame(native, AutoTranslation.Services.TranslatorManager.CurrentTranslator);
            }
            finally
            {
                AutoTranslationCompatPatch.Sentinel = previousSentinel;
                AutoTranslationCompatPatch.NativeTranslator = previousNative;
                AutoTranslation.Services.TranslatorManager.CurrentTranslator = previousCurrent;
                AutoTranslation.Services.TranslatorManager.Ready = previousReady;
            }
        }

        /// <summary>
        /// 鎖住相容層的型別載入防線：框架 DLL 裡任何型別（含編譯器生成的 async 狀態機、closure、快取的 lambda
        /// 委派）都不得以 AutoTranslation 型別當基底類別、介面或欄位型別。
        /// </summary>
        [Test]
        public void FrameworkAssembly_HasNoAutoTranslationTypesInBaseInterfacesOrFields()
        {
            var offenders = new List<string>();
            foreach (Type type in typeof(AutoTranslationCompatPatch).Assembly.GetTypes())
            {
                foreach ((string where, Type dependency) in TypeLoadDependencies(type))
                {
                    if (dependency.Assembly.GetName().Name == "AutoTranslation")
                    {
                        offenders.Add(type.FullName + " " + where + " -> " + dependency.FullName);
                    }
                }
            }

            ClassicAssert.IsEmpty(offenders,
                "這些型別在基底／介面／欄位層級參考了 AutoTranslation，Auto Translation 未安裝時框架會被 RimWorld 拒載或在 DevMode 啟動時報錯。");
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

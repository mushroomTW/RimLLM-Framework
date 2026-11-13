using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// RimTalk 相容層的純邏輯部分：角色對映、訊息轉換、日誌描述與設定登錄表。
/// Harmony 攔截與實際串流需要遊戲執行期，交由實機驗證。
/// </summary>
[TestFixture]
public class CompatRimTalkTests
{
    // 1x1 像素的 JPEG 檔頭片段，只要是合法 base64 即可；轉換器不解碼圖片內容。
    private const string SampleImageBase64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==";


    [Test]
    public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatPatch.PackageId));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(null));

        settings.SetCompatTakeoverEnabled(RimTalkCompatPatch.PackageId, true);
        ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(RimTalkCompatPatch.PackageId));
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));

        settings.SetCompatTakeoverEnabled(RimTalkCompatPatch.PackageId, false);
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatPatch.PackageId));

        // 空鍵不得寫入也不得擲例外
        settings.SetCompatTakeoverEnabled(null, true);
        settings.SetCompatTakeoverEnabled(string.Empty, true);
    }


    /// <summary>
    /// 鎖住相容層的型別載入防線：框架 DLL 裡任何型別（含編譯器生成的 async 狀態機、closure、快取的 lambda
    /// 委派）都不得以 RimTalk 型別當基底類別、介面或欄位型別。
    /// 前兩者由 RimWorld 載入 DLL 時的 Assembly.GetTypes() 解析，RimTalk 缺席時整顆框架被拒載；
    /// 欄位由 DevMode 啟動時 StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes 的 GetFields()
    /// 掃描，RimTalk 缺席時可能每次啟動一條紅字。
    /// 方法簽章與本體延遲到 JIT 才解析，允許出現。
    /// </summary>
    [Test]
    public void FrameworkAssembly_HasNoRimTalkTypesInBaseInterfacesOrFields()
    {
        var offenders = new List<string>();
        foreach (Type type in typeof(RimTalkCompatPatch).Assembly.GetTypes())
        {
            foreach ((string where, Type dependency) in TypeLoadDependencies(type))
            {
                if (dependency.Assembly.GetName().Name == "RimTalk")
                {
                    offenders.Add(type.FullName + " " + where + " -> " + dependency.FullName);
                }
            }
        }

        ClassicAssert.IsEmpty(offenders,
            "這些型別在基底／介面／欄位層級參考了 RimTalk，RimTalk 未安裝時框架會被 RimWorld 拒載或在 DevMode 啟動時報錯。" +
            "async 方法的 RimTalk 型別參數與區域變數會變成狀態機欄位，改成非 async 薄殼（見 RimTalkCompatClient）。");
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

    /// <summary>一個型別及其陣列元素／泛型引數（遞迴）。</summary>
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


    [Test]
    public void RimTalkTarget_DeclaresStablePackageId()
    {
        ClassicAssert.AreEqual("cj.rimtalk", RimTalkCompatPatch.PackageId);
    }
}

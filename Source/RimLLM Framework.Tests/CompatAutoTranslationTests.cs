using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Auto Translation 相容層單元測試：型別載入防線、訊息封裝、參數設定、路由與開關。
/// </summary>
[TestFixture]
public class CompatAutoTranslationTests
{
    [Test]
    public void AutoTranslationTarget_DeclaresStablePackageId()
    {
        ClassicAssert.AreEqual("seohyeon.autotranslation", AutoTranslationCompatTarget.PackageId);
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

    /// <summary>
    /// 鎖住靜態方法簽章：Debug 動作選單（<c>DebugTabMenu_Actions.InitActions</c>）會對所有型別的
    /// <c>Static | Public | NonPublic</c> 方法做 <c>IsDefined</c>（實例方法不在掃描範圍）；簽章含任一相容目標 Mod 的型別時，該 Mod 缺席
    /// 玩家一開 Debug 選單就可能拋 TypeLoadException。目標 Mod 型別只能出現在方法本體。
    /// 涵蓋全部相容目標（AutoTranslation、ModCompatChecker、RimTalk、RimTranslation）。
    /// </summary>
    [Test]
    public void FrameworkAssembly_HasNoCompatTargetTypesInMethodSignatures()
    {
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var targetAssemblies = new HashSet<string> { "AutoTranslation", "ModCompatChecker", "RimTalk", "RimTranslation" };
        var offenders = new List<string>();
        foreach (Type type in typeof(AutoTranslationCompatPatch).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(all))
            {
                var signature = method.GetParameters().Select(p => p.ParameterType).ToList();
                signature.Add(method.ReturnType);
                foreach (Type t in signature.SelectMany(Expand))
                {
                    if (targetAssemblies.Contains(t.Assembly.GetName().Name))
                    {
                        offenders.Add(type.FullName + "." + method.Name + " -> " + t.FullName);
                    }
                }
            }
        }

        ClassicAssert.IsEmpty(offenders,
            "這些方法簽章參考了相容目標 Mod 的型別，目標 Mod 未安裝時開啟 Debug 動作選單會拋 TypeLoadException。");
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

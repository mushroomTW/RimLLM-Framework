using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Api;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests;

/// <summary>
/// Mod 兼容性檢查器相容層單元測試：型別載入防線、訊息封裝、參數設定、路由與開關。
/// </summary>
[TestFixture]
public class CompatModCompatCheckerTests
{
    [Test]
    public void ModCompatCheckerTarget_DeclaresStablePackageId()
    {
        ClassicAssert.AreEqual("modcompatchecker.main", ModCompatCheckerCompatPatch.PackageId);
    }

    [Test]
    public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
    {
        var settings = new RimLLMFrameworkSettings();

        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId));
        settings.SetCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId, true);
        ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId));
        settings.SetCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId, false);
        ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(ModCompatCheckerCompatPatch.PackageId));
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DevTools.Testing;
using LudeonTK;

namespace RimLLM_Framework.InGameTests;

/// <summary>
/// 出貨的 DLL 在 RimWorld 共用 AppDomain（無 binding redirect）內能否完整解析型別。
/// 例如 System.ClientModel 版本與 OpenAI.dll 的 typeref 不一致時，GetTypes 會擲 ReflectionTypeLoadException，
/// headless 測試走 NuGet 解析看不到這類問題。
/// </summary>
[TestFixture(TestType.MainMenu)]
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
// DevTools 只註冊有一個公開建構式的類別：不能宣告私有建構式或改成 static，測試方法因此是實例方法。
#pragma warning disable CA1822, MA0038 // 測試方法不存取實例資料，但必須是實例方法
internal sealed class AssemblyLoadTests
{
    [Test]
    public void EveryShippedAssemblyResolvesAllTypes()
    {
        var failures = new List<string>();
        foreach (Assembly assembly in TestHelpers.FrameworkContent.assemblies.loadedAssemblies)
        {
            try
            {
                assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e.Message).Distinct())
                {
                    failures.Add($"{assembly.GetName().Name}: {message}");
                }
            }
        }
        TestHelpers.AssertNone(failures, "type load failures in shipped assemblies");
    }

    /// <summary>
    /// 重現 Debug 動作選單（<c>DebugTabMenu_Actions.InitActions</c>）的掃描：對每個型別的靜態方法做 <c>IsDefined</c>，
    /// 執行期會因此解析簽章。相容層目標 Mod（RimTalk／Auto Translation／ModCompatChecker）缺席時，
    /// 靜態方法簽章若含它們的型別就會擲 TypeLoadException，玩家一開 Debug 選單就爆。
    /// 要在不啟用這些目標 Mod 的組合下執行才有鑑別力。
    /// </summary>
    [Test]
    public void StaticMethodSignaturesResolveForDebugActionScan()
    {
#pragma warning disable S3011 // 故意掃描非公開靜態方法，重現遊戲 DebugAction 掃描的行為
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
#pragma warning restore S3011
        var failures = new List<string>();
        foreach (Assembly assembly in TestHelpers.FrameworkContent.assemblies.loadedAssemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    try
                    {
                        method.IsDefined(typeof(DebugActionAttribute), inherit: true);
                    }
                    catch (TypeLoadException ex)
                    {
                        failures.Add($"{type.FullName}.{method.Name}: {ex.Message}");
                    }
                }
            }
        }
        TestHelpers.AssertNone(failures, "static method signatures that fail to resolve");
    }
}
#pragma warning restore CA1822, MA0038

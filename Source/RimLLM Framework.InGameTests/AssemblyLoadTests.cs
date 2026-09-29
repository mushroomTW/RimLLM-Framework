using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimTestRedux;

namespace RimLLM_Framework.InGameTests
{
    /// <summary>
    /// 出貨的 DLL 在 RimWorld 共用 AppDomain（無 binding redirect）內能否完整解析型別。
    /// 例如 System.ClientModel 版本與 OpenAI.dll 的 typeref 不一致時，GetTypes 會擲 ReflectionTypeLoadException，
    /// headless 測試走 NuGet 解析看不到這類問題。
    /// </summary>
    [TestSuite]
    internal static class AssemblyLoadTests
    {
        [Test]
        public static void EveryShippedAssemblyResolvesAllTypes()
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
    }
}

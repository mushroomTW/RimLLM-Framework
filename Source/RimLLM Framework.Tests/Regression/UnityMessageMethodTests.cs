using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 回歸測試：Waylith（RimWorld 1.7 的 Unity 相容層）只用 <c>BindingFlags.Instance</c> 尋找
/// Awake／Update 等訊息方法（<c>Waylith/UnityCompat/UnityWorld.cs</c> 的 <c>FindMessage</c>），
/// 宣告成 static 就永遠不會被呼叫。曾因分析器建議把 <see cref="RimLLMDispatcher"/> 的
/// Awake／Update 改成 static，主線程 pump 因此停擺。
/// </summary>
[TestFixture]
public class UnityMessageMethodTests
{
    private static readonly string[] MessageNames =
        ["Awake", "OnEnable", "Start", "Update", "LateUpdate", "OnDisable", "OnApplicationQuit", "OnDestroy", "OnGUI"];

    [Test]
    public void UnityMessageMethodsAreInstanceMethods()
    {
        var staticMessages = new List<string>();
        foreach (Type type in LoadableTypes(typeof(RimLLMDispatcher).Assembly)
            .Where(t => typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(t)))
        {
            foreach (string name in MessageNames)
            {
                MethodInfo method = type.GetMethod(name,
                    BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, Type.EmptyTypes, modifiers: null);
                if (method != null)
                {
                    staticMessages.Add(type.FullName + "." + name);
                }
            }
        }

        Assert.That(staticMessages, Is.Empty,
            "Unity 訊息方法必須是執行個體方法，static 版本不會被 Waylith 呼叫；CA1822／S2325 的建議請用 #pragma 抑制。");
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null);
        }
    }
}

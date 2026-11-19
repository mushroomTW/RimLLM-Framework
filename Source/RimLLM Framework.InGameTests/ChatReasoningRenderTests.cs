using System.Collections;
using System.Linq;
using System.Reflection;
using DevTools.Testing;
using RimLLM_Framework.Mod;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.InGameTests;

/// <summary>
/// 在遊戲的 Unity 與翻譯環境驗證對話測試頁：供應商原生推理以欄位存放後，
/// 仍能畫成灰色的思考區塊，並和答案分開。
/// </summary>
[TestFixture(TestType.MainMenu)]
internal sealed class ChatReasoningRenderTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;

    private static string[] Render(string reasoning, string answer)
    {
        string body = ChatTestDrawer.BuildMarkdownBody(reasoning, answer)
            .Substring(ChatTestDrawer.MarkdownBodyMarker.Length);
        var style = new GUIStyle(Text.fontStyles[(int)GameFont.Small]) { richText = true, wordWrap = true };
        var parts = (IList)typeof(ChatTestDrawer).GetMethod("RenderMarkdownBody", Flags)
            .Invoke(null, new object[] { body, style, 400f });
        return parts.Cast<object>()
            .Select(p => (string)p.GetType().GetField("Rich").GetValue(p) ?? string.Empty)
            .ToArray();
    }

    [Test]
    public void ClosedReasoningRendersAsSilverBlockBeforeAnswer()
    {
        string[] parts = Render("weighing options", "final answer");
        string all = string.Join("\n", parts);

        Expect.IsTrue(parts[0].Contains("<color=silver>"));
        Expect.IsTrue(parts[0].Contains("weighing options"));
        Expect.IsTrue(parts[0].Contains("RimLLM_ThinkProcessLabel".Translate().ToString()));
        Expect.IsTrue(parts.Last().Contains("final answer"));
        Expect.IsFalse(parts.Last().Contains("weighing options"));
        Expect.IsFalse(all.Contains("<think>"));
    }

    [Test]
    public void ReasoningWithoutAnswerRendersAsThinking()
    {
        string all = string.Join("\n", Render("still thinking", ""));

        Expect.IsTrue(all.Contains("RimLLM_ThinkingLabel".Translate().ToString()));
        Expect.IsTrue(all.Contains("still thinking"));
    }

    [Test]
    public void InlineThinkTagsInAnswerStillRender()
    {
        string all = string.Join("\n", Render("", "<think>inline</think>answer"));

        Expect.IsTrue(all.Contains("<color=silver>"));
        Expect.IsTrue(all.Contains("inline"));
        Expect.IsFalse(all.Contains("<think>"));
    }

    [Test]
    public void ProviderColorTagsInReasoningCannotCloseFrameworkWrapper()
    {
        string all = string.Join("\n", Render("bad </color><color=red>x", "ok"));

        Expect.IsFalse(all.Contains("<color=red>"));
    }
}

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
[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
// DevTools 只註冊有一個公開建構式的類別：不能宣告私有建構式或改成 static，測試方法因此是實例方法。
#pragma warning disable CA1822, MA0038 // 測試方法不存取實例資料，但必須是實例方法
internal sealed class ChatReasoningRenderTests
{
#pragma warning disable S3011 // 故意存取非公開的繪製方法，以驗證實際的渲染結果
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
#pragma warning restore S3011

    private static string[] Render(string reasoning, string answer)
    {
        string body = ChatTestDrawer.BuildMarkdownBody(reasoning, answer)
            .Substring(ChatTestDrawer.MarkdownBodyMarker.Length);
        var style = new GUIStyle(Text.fontStyles[(int)GameFont.Small]) { richText = true, wordWrap = true };
        var parts = (IList)typeof(ChatTestDrawer).GetMethod("RenderMarkdownBody", Flags)
            .Invoke(null, [body, style, 400f]);
        return [.. parts.Cast<object>().Select(p => (string)p.GetType().GetField("Rich").GetValue(p) ?? string.Empty)];
    }

    [Test]
    public void ClosedReasoningRendersAsSilverBlockBeforeAnswer()
    {
        string[] parts = Render("weighing options", "final answer");
        string all = string.Join('\n', parts);

        Expect.IsTrue(parts[0].Contains("<color=silver>", System.StringComparison.Ordinal));
        Expect.IsTrue(parts[0].Contains("weighing options", System.StringComparison.Ordinal));
        Expect.IsTrue(parts[0].Contains("RimLLM_ThinkProcessLabel".Translate().ToString(), System.StringComparison.Ordinal));
        Expect.IsTrue(parts[^1].Contains("final answer", System.StringComparison.Ordinal));
        Expect.IsFalse(parts[^1].Contains("weighing options", System.StringComparison.Ordinal));
        Expect.IsFalse(all.Contains("<think>", System.StringComparison.Ordinal));
    }

    [Test]
    public void ReasoningWithoutAnswerRendersAsThinking()
    {
        string all = string.Join('\n', Render("still thinking", ""));

        Expect.IsTrue(all.Contains("RimLLM_ThinkingLabel".Translate().ToString(), System.StringComparison.Ordinal));
        Expect.IsTrue(all.Contains("still thinking", System.StringComparison.Ordinal));
    }

    [Test]
    public void InlineThinkTagsInAnswerStillRender()
    {
        string all = string.Join('\n', Render("", "<think>inline</think>answer"));

        Expect.IsTrue(all.Contains("<color=silver>", System.StringComparison.Ordinal));
        Expect.IsTrue(all.Contains("inline", System.StringComparison.Ordinal));
        Expect.IsFalse(all.Contains("<think>", System.StringComparison.Ordinal));
    }

    [Test]
    public void ProviderColorTagsInReasoningCannotCloseFrameworkWrapper()
    {
        string all = string.Join('\n', Render("bad </color><color=red>x", "ok"));

        Expect.IsFalse(all.Contains("<color=red>", System.StringComparison.Ordinal));
    }
}
#pragma warning restore CA1822, MA0038

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// JSON 修復（RepairJson）的行為測試。
/// 重點在於字串內外必須分開判斷：模型常在輸出 JSON 前先吐一段思考內容，
/// 這些思考區塊要被移除，但字串本身（含有相同文字時）不能被誤刪。
/// </summary>
[TestFixture]
public class JsonRepairTests
{
    [Test]
    public void ThinkBlockBeforeJsonIsRemoved()
    {
        string repaired = RimLLMJsonHelper.RepairJson("<think>reasoning</think>{\"a\":\"x\"}");
        ClassicAssert.AreEqual("{\"a\":\"x\"}", repaired);
    }

    [Test]
    public void ThinkBlockAfterFirstStringIsRemoved()
    {
        // 回歸測試：字串結束後必須回到非字串分支，否則後續的思考區塊不會被移除。
        string repaired = RimLLMJsonHelper.RepairJson("{\"a\":\"x\"}<think>reasoning</think>{\"b\":1}");
        ClassicAssert.AreEqual("{\"a\":\"x\"}{\"b\":1}", repaired);
    }

    [Test]
    public void ThinkBlockBetweenPropertiesIsRemoved()
    {
        string repaired = RimLLMJsonHelper.RepairJson("{\"a\":\"x\",<think>reasoning</think>\"b\":1}");
        ClassicAssert.AreEqual("{\"a\":\"x\",\"b\":1}", repaired);
    }

    [Test]
    public void ThinkLikeTextInsideStringIsKept()
    {
        // 字串內容本身含 <think> 時不應被當成思考區塊移除。
        string repaired = RimLLMJsonHelper.RepairJson("{\"a\":\"<think>not a block</think>\"}");
        ClassicAssert.AreEqual("{\"a\":\"<think>not a block</think>\"}", repaired);
    }

    [Test]
    public void TrailingCommaAfterStringIsStillRemoved()
    {
        // 字串結束後仍須回到非字串分支，否則尾逗號清理會失效。
        string repaired = RimLLMJsonHelper.RepairJson("{\"a\":\"x\",}");
        ClassicAssert.AreEqual("{\"a\":\"x\"}", repaired);
    }
}

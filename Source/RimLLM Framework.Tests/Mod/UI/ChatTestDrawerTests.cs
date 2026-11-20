using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests;

[TestFixture]
public class ChatTestDrawerTests
{
    [Test]
    public void ChatEntryMeta_RoundTripSerialization_PreservesAllFields()
    {
        var meta = new ChatTestDrawer.ChatEntryMeta
        {
            ModelId = "Gemini:gemini-2.5-flash",
            ElapsedMs = 1250,
            TotalTokens = 350,
            PromptTokens = 50,
            CompletionTokens = 300,
            IsEstimatedTokens = false
        };

        string serialized = meta.Serialize();
        var reloaded = ChatTestDrawer.ChatEntryMeta.Deserialize(serialized);

        ClassicAssert.IsNotNull(reloaded);
        ClassicAssert.AreEqual("Gemini:gemini-2.5-flash", reloaded.ModelId);
        ClassicAssert.AreEqual(1250, reloaded.ElapsedMs);
        ClassicAssert.AreEqual(350, reloaded.TotalTokens);
        ClassicAssert.AreEqual(50, reloaded.PromptTokens);
        ClassicAssert.AreEqual(300, reloaded.CompletionTokens);
        ClassicAssert.IsFalse(reloaded.IsEstimatedTokens);
    }

    [Test]
    public void ChatEntryMeta_EstimatedTokens_PreservedCorrectly()
    {
        var meta = new ChatTestDrawer.ChatEntryMeta
        {
            ModelId = "OpenAI:gpt-4o",
            ElapsedMs = 800,
            TotalTokens = 120,
            PromptTokens = 40,
            CompletionTokens = 80,
            IsEstimatedTokens = true
        };

        string serialized = meta.Serialize();
        var reloaded = ChatTestDrawer.ChatEntryMeta.Deserialize(serialized);

        ClassicAssert.IsNotNull(reloaded);
        ClassicAssert.IsTrue(reloaded.IsEstimatedTokens);
    }

    [Test]
    public void FormatModelDisplayName_SplitsProviderAndModel_WhenColonPresent()
    {
        string formatted = ChatTestDrawer.FormatModelDisplayName("Gemini:gemini-2.5-flash");
        ClassicAssert.AreEqual("Gemini • gemini-2.5-flash", formatted);

        string openAi = ChatTestDrawer.FormatModelDisplayName("OpenAI:gpt-4o-mini");
        ClassicAssert.AreEqual("OpenAI • gpt-4o-mini", openAi);
    }

    [Test]
    public void FormatModelDisplayName_ReturnsOriginal_WhenNoColon()
    {
        string formatted = ChatTestDrawer.FormatModelDisplayName("gemini-2.5-flash");
        ClassicAssert.AreEqual("gemini-2.5-flash", formatted);

        ClassicAssert.AreEqual("", ChatTestDrawer.FormatModelDisplayName(""));
        ClassicAssert.AreEqual("", ChatTestDrawer.FormatModelDisplayName(modelId: null));
    }

    [Test]
    public void ParseMessage_ExtractsMetaAndCleansBody()
    {
        var meta = new ChatTestDrawer.ChatEntryMeta
        {
            ModelId = "DeepSeek:deepseek-chat",
            ElapsedMs = 2100,
            TotalTokens = 500,
            PromptTokens = 100,
            CompletionTokens = 400,
            IsEstimatedTokens = false
        };

        string rawEntry = "<b>[AI]:</b> 這是 AI 的回覆內容" + ChatTestDrawer.MetaPrefix + meta.Serialize() + ChatTestDrawer.MetaSuffix;

        bool isUser = ChatTestDrawer.ParseMessage(rawEntry, out _, out string body, out var parsedMeta);

        ClassicAssert.IsFalse(isUser, "AI 訊息應判定為 false");
        ClassicAssert.AreEqual("這是 AI 的回覆內容", body, "Body 必須完全抽乾淨中繼標籤");
        ClassicAssert.IsNotNull(parsedMeta);
        ClassicAssert.AreEqual("DeepSeek:deepseek-chat", parsedMeta.ModelId);
        ClassicAssert.AreEqual(2100, parsedMeta.ElapsedMs);
        ClassicAssert.AreEqual(500, parsedMeta.TotalTokens);
    }

    /// <summary>
    /// 推理內容以長度標記和答案分開存放：內容含 --&gt;、&lt;think&gt; 或標記本身都不能讓分界錯位。
    /// </summary>
    [TestCase("", "answer")]
    [TestCase("plain reasoning", "answer")]
    [TestCase("tricky --> <think>x</think> <!--rimllm-reasoning:3-->", "<think>inline</think>answer")]
    [TestCase("only reasoning", "")]
    public void MarkdownBody_RoundTripsReasoningAndAnswer(string reasoning, string answer)
    {
        string built = ChatTestDrawer.BuildMarkdownBody(reasoning, answer);
        StringAssert.StartsWith(ChatTestDrawer.MarkdownBodyMarker, built);

        ChatTestDrawer.SplitMarkdownBody(built.Substring(ChatTestDrawer.MarkdownBodyMarker.Length),
            out string parsedReasoning, out string parsedAnswer);

        ClassicAssert.AreEqual(reasoning, parsedReasoning);
        ClassicAssert.AreEqual(answer, parsedAnswer);
    }

    /// <summary>標記損壞（長度超出、不是數字）時不能擲出例外，整段退回當答案顯示。</summary>
    [TestCase("<!--rimllm-reasoning:999-->short")]
    [TestCase("<!--rimllm-reasoning:-1-->x")]
    [TestCase("<!--rimllm-reasoning:abc-->x")]
    public void SplitMarkdownBody_MalformedMarkerFallsBackToAnswer(string body)
    {
        ChatTestDrawer.SplitMarkdownBody(body, out string reasoning, out string answer);

        ClassicAssert.AreEqual(string.Empty, reasoning);
        ClassicAssert.AreEqual(body, answer);
    }

    [Test]
    public void ParseMessage_WithoutMeta_ParsesNormally()
    {
        string rawEntry = "<b>[AI]:</b> 一般舊訊息";
        bool isUser = ChatTestDrawer.ParseMessage(rawEntry, out _, out string body, out var parsedMeta);

        ClassicAssert.IsFalse(isUser);
        ClassicAssert.AreEqual("一般舊訊息", body);
        ClassicAssert.IsNull(parsedMeta);
    }

    [Test]
    public void ParseMessage_UserMessage_ParsesNormally()
    {
        string rawEntry = "<b>[我]:</b> 你好呀";
        bool isUser = ChatTestDrawer.ParseMessage(rawEntry, out _, out string body, out var parsedMeta);

        ClassicAssert.IsTrue(isUser);
        ClassicAssert.AreEqual("你好呀", body);
        ClassicAssert.IsNull(parsedMeta);
    }
}

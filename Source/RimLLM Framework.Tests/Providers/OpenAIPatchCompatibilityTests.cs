#pragma warning disable OPENAI001 // OpenAIContext 為評估用 API，只用於測試序列化。
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using OpenAI.Chat;
using RimLLM_Framework.Providers;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 釘住本專案對 <see cref="ChatCompletionOptions.Patch"/> 的實際用法在目前 OpenAI SDK 上可用。
/// 舊版 SDK 曾在 Patch 根屬性時對 null 子屬性傳播而擲 NRE（當時以反射繞過）；升級 SDK 後若此處失敗，
/// 代表 Patch 行為又變了，必須在升級前處理。
/// </summary>
[TestFixture]
public sealed class OpenAIPatchCompatibilityTests
{
    /// <summary>模擬 MEAI 轉換後已帶值的選項，Patch 要能在這種狀態下操作根屬性。</summary>
    private static ChatCompletionOptions CreatePopulatedOptions()
    {
        var options = new ChatCompletionOptions { MaxOutputTokenCount = 256 };
        options.Tools.Add(ChatTool.CreateFunctionTool("AddNumbers", "Adds two numbers"));
        options.Metadata["k"] = "v";
        return options;
    }

    private static JsonObject Serialize(ChatCompletionOptions options)
        => JsonNode.Parse(ModelReaderWriter.Write(options, new ModelReaderWriterOptions("J"), OpenAI.OpenAIContext.Default).ToString()).AsObject();

    [Test]
    public void RewritesMaxTokensFieldOnPopulatedOptions()
    {
        ChatCompletionOptions options = CreatePopulatedOptions();
        options.Patch.Remove(OpenAIPatchPaths.MaxCompletionTokens);
        options.Patch.Set(OpenAIPatchPaths.MaxTokens, 256);

        JsonObject json = Serialize(options);
        ClassicAssert.IsNull(json["max_completion_tokens"]);
        ClassicAssert.AreEqual(256, (int)json["max_tokens"]);
    }

    [Test]
    public void WritesRootFieldsUsedByReasoningDialectsAndStructuredOutput()
    {
        ChatCompletionOptions options = CreatePopulatedOptions();
        options.Patch.Set(OpenAIPatchPaths.ResponseFormat, Encoding.UTF8.GetBytes("{\"type\":\"json_object\"}"));
        options.Patch.Set(OpenAIPatchPaths.ReasoningEffort, "high");
        options.Patch.Set(OpenAIPatchPaths.Thinking, Encoding.UTF8.GetBytes("{\"type\":\"enabled\"}"));
        options.Patch.Set(OpenAIPatchPaths.EnableThinking, true);
        options.Patch.Set(OpenAIPatchPaths.Models, Encoding.UTF8.GetBytes("[\"a\",\"b\"]"));
        options.Patch.Remove(OpenAIPatchPaths.StreamOptions);
        options.Patch.Remove(OpenAIPatchPaths.Reasoning);

        JsonObject json = Serialize(options);
        ClassicAssert.AreEqual("json_object", (string)json["response_format"]["type"]);
        ClassicAssert.AreEqual("high", (string)json["reasoning_effort"]);
        ClassicAssert.AreEqual("enabled", (string)json["thinking"]["type"]);
        ClassicAssert.IsTrue((bool)json["enable_thinking"]);
        ClassicAssert.AreEqual(2, json["models"].AsArray().Count);
        ClassicAssert.IsNull(json["stream_options"]);
    }

    [Test]
    public void ChainPatchKeepsBaseFactoryPatches()
    {
        var chained = OpenAIPatchExtensions.ChainPatch(
            OpenAIPatchExtensions.ChainPatch(_ => CreatePopulatedOptions(), o => o.Patch.Set(OpenAIPatchPaths.ReasoningEffort, "low")),
            o => o.Patch.Set(OpenAIPatchPaths.MaxTokens, 64));

        JsonObject json = Serialize((ChatCompletionOptions)chained(null));
        ClassicAssert.AreEqual("low", (string)json["reasoning_effort"]);
        ClassicAssert.AreEqual(64, (int)json["max_tokens"]);
    }
}

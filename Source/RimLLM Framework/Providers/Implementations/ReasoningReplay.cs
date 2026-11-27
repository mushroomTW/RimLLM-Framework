using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Providers;

/// <summary>工具迴圈中把推理內容原樣送回產生它的模型。</summary>
/// <remarks>
/// 不送回時：DeepSeek 帶 tools 的請求回 400（api-docs.deepseek.com/guides/thinking_mode）、
/// Gemini 3 缺 thought_signature 回 400（ai.google.dev 的 thought-signatures）、OpenRouter 失去跨步推理。
/// Microsoft.Extensions.AI.OpenAI 10.11.0 的 Chat Completions 轉換只送文字與工具呼叫，這些欄位全部遺失。
/// 內容連同來源存進 <see cref="TextReasoningContent.ProtectedData"/>：它會跟著對話序列化，
/// 而加密推理與簽章只有原模型解得開，備援換模型後必須能辨認並丟棄。
/// </remarks>
internal static class ReasoningReplay
{
    private const string ExtraContentKey = "rimllm_extra_content";
    private const string ReasoningField = "reasoning";
    private const string ReasoningContentField = "reasoning_content";

    /// <summary>Google 文件允許的替代簽章，用於從其他模型轉來、沒有簽章的工具呼叫。</summary>
    private const string GeminiForeignCallExtraContent =
        "{\"google\":{\"thought_signature\":\"skip_thought_signature_validator\"}}";

    internal static string Origin(string providerId, string model, bool responses) =>
        providerId + "|" + model + "|" + (responses ? "responses" : "chat");

    private static string Seal(string origin, JsonObject payload) =>
        new JsonObject { ["rimllm"] = origin, ["data"] = payload }.ToJsonString();

    private static JsonObject? Open(string? sealedData, string origin)
    {
        if (string.IsNullOrEmpty(sealedData) || sealedData[0] != '{') return null;
        try
        {
            return JsonNode.Parse(sealedData) is JsonObject root && (string?)root["rimllm"] == origin
                ? root["data"] as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static void CaptureResponses(IEnumerable<AIContent> contents, string origin)
    {
        foreach (TextReasoningContent reasoning in contents.OfType<TextReasoningContent>())
        {
            if (string.IsNullOrEmpty(reasoning.ProtectedData)) continue;
            reasoning.ProtectedData = Seal(origin, new JsonObject { ["encrypted"] = reasoning.ProtectedData });
        }
    }

    internal static void CaptureChat(ChatResponse response, string origin, bool replayReasoning)
    {
        if (response.RawRepresentation is not ChatCompletion raw || response.Messages.Count == 0) return;
        IList<AIContent> contents = response.Messages[response.Messages.Count - 1].Contents;

        foreach (ChatToolCall call in raw.ToolCalls)
        {
            if (call.Patch.TryGetJson("$.extra_content"u8, out ReadOnlyMemory<byte> extra) && !extra.IsEmpty)
            {
                AttachExtraContent(contents, call.Id, Encoding.UTF8.GetString(extra.Span), origin);
            }
        }

        if (!replayReasoning) return;
        JsonArray? details = raw.Patch.TryGetJson("$.choices[0].message.reasoning_details"u8, out ReadOnlyMemory<byte> json)
            ? ParseArray(json)
            : null;
        TextReasoningContent? reasoning = contents.OfType<TextReasoningContent>().FirstOrDefault();
        if (reasoning == null && details == null) return;
        if (reasoning == null)
        {
            reasoning = new TextReasoningContent(string.Empty);
            contents.Insert(0, reasoning);
        }
        bool plainField = raw.Patch.TryGetValue("$.choices[0].message.reasoning"u8, out string? plain) && !string.IsNullOrEmpty(plain);
        reasoning.ProtectedData = SealChat(origin, plainField, details);
    }

    private static string SealChat(string origin, bool plainField, JsonArray? details)
    {
        var payload = new JsonObject { ["field"] = plainField ? ReasoningField : ReasoningContentField };
        if (details != null) payload["details"] = details;
        return Seal(origin, payload);
    }

    private static JsonArray? ParseArray(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonNode.Parse(json.Span) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AttachExtraContent(IEnumerable<AIContent> contents, string callId, string json, string origin)
    {
        FunctionCallContent? call = contents.OfType<FunctionCallContent>()
            .FirstOrDefault(c => string.Equals(c.CallId, callId, StringComparison.Ordinal));
        if (call != null)
        {
            (call.AdditionalProperties ??= [])[ExtraContentKey] = Seal(origin, new JsonObject { ["extra"] = json });
        }
    }

    /// <summary>串流的欄位散在多個 chunk，收到結束 chunk 時才補上一筆不含文字的推理內容。</summary>
    internal sealed class ChatStreamCapture(string origin, bool replayReasoning)
    {
        private readonly Dictionary<int, string> _callIds = [];
        private readonly Dictionary<int, string> _extras = [];
        private JsonArray? _details;
        private bool _plainField;
        private bool _sawReasoning;

        public void Observe(ChatResponseUpdate update)
        {
            if (update.RawRepresentation is StreamingChatCompletionUpdate raw) ObserveRaw(raw);
            _sawReasoning |= update.Contents.Any(c => c is TextReasoningContent { Text.Length: > 0 });

            foreach (KeyValuePair<int, string> extra in _extras)
            {
                if (_callIds.TryGetValue(extra.Key, out string? callId))
                {
                    AttachExtraContent(update.Contents, callId, extra.Value, origin);
                }
            }

            if (replayReasoning && update.FinishReason != null && (_sawReasoning || _details != null))
            {
                update.Contents.Add(new TextReasoningContent(string.Empty) { ProtectedData = SealChat(origin, _plainField, _details) });
                _sawReasoning = false;
                _details = null;
            }
        }

        private void ObserveRaw(StreamingChatCompletionUpdate raw)
        {
            foreach (StreamingChatToolCallUpdate call in raw.ToolCallUpdates)
            {
                if (!string.IsNullOrEmpty(call.ToolCallId)) _callIds[call.Index] = call.ToolCallId;
                if (call.Patch.TryGetJson("$.extra_content"u8, out ReadOnlyMemory<byte> extra) && !extra.IsEmpty)
                {
                    _extras[call.Index] = Encoding.UTF8.GetString(extra.Span);
                }
            }
            _plainField |= raw.Patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? plain) && !string.IsNullOrEmpty(plain);
            if (raw.Patch.TryGetJson("$.choices[0].delta.reasoning_details"u8, out ReadOnlyMemory<byte> json)
                && ParseArray(json) is { } parts)
            {
                foreach (JsonNode? part in parts) MergeDetail(_details ??= [], part);
            }
        }

        private static void MergeDetail(JsonArray details, JsonNode? part)
        {
            if (part is not JsonObject incoming) return;
            JsonObject? target = incoming["index"] is { } index
                ? details.OfType<JsonObject>().FirstOrDefault(d =>
                    JsonNode.DeepEquals(d["index"], index) && JsonNode.DeepEquals(d["type"], incoming["type"]))
                : null;
            if (target == null)
            {
                details.Add(incoming.DeepClone());
                return;
            }
            foreach (KeyValuePair<string, JsonNode?> field in incoming)
            {
                if (field.Value == null) continue;
                bool append = field.Key is "text" or "summary" or "data"
                    && target[field.Key]?.GetValueKind() == JsonValueKind.String
                    && field.Value.GetValueKind() == JsonValueKind.String;
                target[field.Key] = append
                    ? (string?)target[field.Key] + (string?)field.Value
                    : field.Value.DeepClone();
            }
        }
    }

    internal static List<ChatMessage> PrepareResponses(IEnumerable<ChatMessage> messages, string origin, bool replay)
    {
        var prepared = new List<ChatMessage>();
        foreach (ChatMessage message in messages)
        {
            if (message.Role != ChatRole.Assistant)
            {
                prepared.Add(message);
                continue;
            }
            List<AIContent> contents = [.. message.Contents.Select(c => PrepareResponsesContent(c, origin, replay)).OfType<AIContent>()];
            if (contents.Count == 0) continue;
            prepared.Add(new ChatMessage(ChatRole.Assistant, contents) { AuthorName = message.AuthorName });
        }
        return prepared;
    }

    // 原始 ResponseItem 帶著伺服端 id；store=false 時那些 id 查不到，換供應商後更不成立，所以一律重建內容。
    private static AIContent? PrepareResponsesContent(AIContent content, string origin, bool replay)
    {
        switch (content)
        {
            case TextReasoningContent reasoning:
                if (!replay || (string?)Open(reasoning.ProtectedData, origin)?["encrypted"] is not { } encrypted) return null;
                var copy = new TextReasoningContent(reasoning.Text) { ProtectedData = encrypted };
                foreach (KeyValuePair<string, object?> pair in reasoning.AdditionalProperties ?? [])
                {
                    // 對話存檔還原後字串會變成 JsonElement，SDK 只認得 string。
                    (copy.AdditionalProperties ??= [])[pair.Key] =
                        pair.Value is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : pair.Value;
                }
                return copy;
            case FunctionCallContent call:
                return new FunctionCallContent(call.CallId, call.Name, call.Arguments);
            case TextContent text:
                return new TextContent(text.Text);
            default:
                return content;
        }
    }

    internal static List<ChatMessage> PrepareChat(IEnumerable<ChatMessage> messages, string origin, bool geminiSignatures) =>
        [.. messages.Select(m => m.Role == ChatRole.Assistant ? PrepareChatMessage(m, origin, geminiSignatures) : m)];

    private static ChatMessage PrepareChatMessage(ChatMessage message, string origin, bool geminiSignatures)
    {
        JsonObject? envelope = message.Contents.OfType<TextReasoningContent>()
            .Select(r => Open(r.ProtectedData, origin)).FirstOrDefault(o => o != null);
        List<FunctionCallContent> calls = [.. message.Contents.OfType<FunctionCallContent>()];
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (FunctionCallContent call in calls)
        {
            if (!string.IsNullOrEmpty(call.CallId) && ReadExtraContent(call, origin) is { } extra) extras[call.CallId] = extra;
        }
        if (geminiSignatures && calls.Count > 0 && extras.Count == 0 && !string.IsNullOrEmpty(calls[0].CallId))
        {
            extras[calls[0].CallId] = GeminiForeignCallExtraContent;
        }
        if (envelope == null && extras.Count == 0) return message;

        AssistantChatMessage? converted = new[] { message }.AsOpenAIChatMessages().OfType<AssistantChatMessage>().SingleOrDefault();
        if (converted == null) return message;

        if (envelope != null) ApplyEnvelope(converted, envelope, message);
        foreach (ChatToolCall call in converted.ToolCalls)
        {
            if (extras.TryGetValue(call.Id, out string? extra)) call.Patch.Set("$.extra_content"u8, Encoding.UTF8.GetBytes(extra));
        }
        return new ChatMessage(ChatRole.Assistant, message.Contents) { AuthorName = message.AuthorName, RawRepresentation = converted };
    }

    private static void ApplyEnvelope(AssistantChatMessage converted, JsonObject envelope, ChatMessage message)
    {
        if (envelope["details"] is JsonArray details)
        {
            converted.Patch.Set("$.reasoning_details"u8, Encoding.UTF8.GetBytes(details.ToJsonString()));
            return;
        }
        string text = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
        if ((string?)envelope["field"] == ReasoningField) converted.Patch.Set("$.reasoning"u8, text);
        else converted.Patch.Set("$.reasoning_content"u8, text);
    }

    private static string? ReadExtraContent(FunctionCallContent call, string origin)
    {
        if (call.AdditionalProperties == null || !call.AdditionalProperties.TryGetValue(ExtraContentKey, out object? value)) return null;
        string? sealedData = value as string ?? (value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : null);
        return (string?)Open(sealedData, origin)?["extra"];
    }
}

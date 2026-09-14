using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;
using RimTalk.Data;

namespace RimLLM_Framework.Compat
{
    /// <summary>
    /// 把 RimTalk 的 <c>(Role, string)</c> 訊息列轉成 MEAI <see cref="ChatMessage"/>。
    /// 純轉換邏輯，不碰網路與 RimWorld 執行期，供單元測試直接驗證。
    /// </summary>
    internal static class RimTalkMessageConverter
    {
        /// <summary>RimTalk 截圖一律以 JPEG base64 傳遞（見其 ChatMessage.ToPayload 的 data:image/jpeg）。</summary>
        private const string ImageMediaType = "image/jpeg";

        public static ChatRole ToChatRole(Role role)
        {
            switch (role)
            {
                case Role.System: return ChatRole.System;
                case Role.AI: return ChatRole.Assistant;
                default: return ChatRole.User;
            }
        }

        /// <summary>
        /// 合併 prefix 與 messages 後轉為 MEAI 訊息。
        /// 與 RimTalk 原生 OpenAIClient.BuildMessages 行為一致：連續同角色訊息以空行合併成一則
        /// （部分供應商拒絕連續同角色訊息）；圖片掛在最後一則 user 訊息上，若最後一則不是 user
        /// 則另外補一則只含圖片的 user 訊息。
        /// </summary>
        public static List<ChatMessage> Build(
            List<(Role role, string message)> prefixMessages,
            List<(Role role, string message)> messages,
            string imageBase64)
        {
            var merged = new List<(ChatRole role, string text)>();
            AppendMerged(merged, prefixMessages);
            AppendMerged(merged, messages);

            var result = new List<ChatMessage>(merged.Count + 1);
            foreach ((ChatRole role, string text) in merged)
            {
                result.Add(new ChatMessage(role, text));
            }

            if (!string.IsNullOrEmpty(imageBase64))
            {
                var image = new DataContent(Convert.FromBase64String(imageBase64), ImageMediaType);
                ChatMessage last = result.Count > 0 ? result[result.Count - 1] : null;
                if (last != null && last.Role == ChatRole.User)
                {
                    last.Contents.Add(image);
                }
                else
                {
                    result.Add(new ChatMessage(ChatRole.User, new List<AIContent> { image }));
                }
            }
            return result;
        }

        private static void AppendMerged(List<(ChatRole role, string text)> target, List<(Role role, string message)> source)
        {
            if (source == null) return;
            foreach ((Role role, string message) in source)
            {
                ChatRole chatRole = ToChatRole(role);
                string text = message ?? string.Empty;
                if (target.Count > 0 && target[target.Count - 1].role == chatRole)
                {
                    (ChatRole _, string existing) = target[target.Count - 1];
                    target[target.Count - 1] = (chatRole, existing + "\n\n" + text);
                }
                else
                {
                    target.Add((chatRole, text));
                }
            }
        }

        /// <summary>
        /// 產生寫進 RimTalk API Log 的請求描述。維持 OpenAI 風格的 {model, stream, messages[{role, content}]}
        /// 形狀，讓 RimTalk 的 Debug 視窗照常可讀；圖片只標記存在，不把整段 base64 塞進日誌。
        /// </summary>
        public static string DescribeRequest(IReadOnlyList<ChatMessage> messages, bool stream, string model)
        {
            var payloadMessages = new List<Dictionary<string, object>>(messages.Count);
            foreach (ChatMessage message in messages)
            {
                var entry = new Dictionary<string, object>
                {
                    ["role"] = message.Role.Value,
                    ["content"] = message.Text
                };
                foreach (AIContent content in message.Contents)
                {
                    if (content is DataContent data && data.HasTopLevelMediaType("image"))
                    {
                        entry["image"] = $"<{data.MediaType}, {data.Data.Length} bytes>";
                        break;
                    }
                }
                payloadMessages.Add(entry);
            }

            return RimLLMJson.Serialize(new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = stream,
                ["messages"] = payloadMessages
            });
        }
    }
}

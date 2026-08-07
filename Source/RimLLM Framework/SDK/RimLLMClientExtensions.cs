using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.SDK
{
    /// <summary>RimLLM SDK 對 IChatClient 的擴充方法。</summary>
    public static class RimLLMClientExtensions
    {
        /// <summary>結構化輸出 schema 的自動快取（取代舊 RegisterResponseType 預熱）。</summary>
        private static readonly ConcurrentDictionary<Type, string> SchemaCache = new ConcurrentDictionary<Type, string>();

        /// <summary>
        /// 結構化輸出：以目標型別 T 產生 JSON Schema 送出，並回傳反序列化結果。
        /// client 為 RimLLMProvider.CreateChatClient 回傳的 facade 時走完整路徑
        /// （含 JSON repair 與 LLM-assisted double-repair）；其他 IChatClient 走簡化路徑（schema + repair）。
        /// </summary>
        public static Task<T> GetResponseObjectAsync<T>(
            this IChatClient client,
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            if (client is RimLLMChatClient facade)
            {
                return facade.GenerateObjectAsync<T>(messages, options, cancellationToken);
            }

            return SimplifiedPathAsync<T>(client, messages, options, cancellationToken);
        }

        private static async Task<T> SimplifiedPathAsync<T>(
            IChatClient client,
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options,
            CancellationToken cancellationToken)
        {
            ChatOptions effective = options ?? new RimLLMChatOptions();
            string schemaJson = SchemaCache.GetOrAdd(typeof(T), t =>
                RimLLMJsonHelper.GenerateJsonSchema(t, uppercaseTypes: false).ToString());
            using (JsonDocument document = JsonDocument.Parse(schemaJson))
            {
                effective.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                    document.RootElement.Clone(),
                    "custom_type",
                    "RimLLM structured response");
            }

            ChatResponse response = await client
                .GetResponseAsync(messages, effective, cancellationToken)
                .ConfigureAwait(false);

            string raw = response?.Text ?? string.Empty;
            try
            {
                return RimLLMManager.DeserializeAndValidate<T>(raw);
            }
            catch (Exception)
            {
                string repaired = RimLLMJsonHelper.RepairJson(raw);
                return RimLLMManager.DeserializeAndValidate<T>(RimLLMJsonHelper.ExtractJsonBlock(repaired));
            }
        }
    }
}

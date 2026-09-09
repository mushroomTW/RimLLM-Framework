using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
    /// <summary>RimLLM SDK 對 IChatClient 的擴充方法。</summary>
    public static class RimLLMClientExtensions
    {
        /// <summary>
        /// 結構化輸出：以目標型別 T 產生 JSON Schema 送出，並回傳反序列化結果。
        /// client 為 RimLLMProvider.CreateChatClient 回傳的 facade 時走完整路徑
        /// （含 JSON repair）；其他 IChatClient 走簡化路徑（schema + repair）。
        /// </summary>
        public static Task<T> GetResponseObjectAsync<T>(
            this IChatClient client,
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            // 以 GetService 而非型別轉換辨識框架堆疊：MEAI 的 DelegatingChatClient 會把
            // GetService 往內層轉發，因此無論外面包了幾層中介層都認得出來。
            if (client.GetService(typeof(RimLLMFailoverChatClient)) is RimLLMFailoverChatClient)
            {
                return FrameworkPathAsync<T>(client, messages, options, cancellationToken);
            }

            return SimplifiedPathAsync<T>(client, messages, options, cancellationToken);
        }

        /// <summary>
        /// 框架路徑：只在 options 上標註目標型別，schema 方言交給知道供應商身分的路由層決定。
        /// 請求本身走完整的 IChatClient 鏈，因此防濫用、預算、佇列與快取都不會被繞過
        /// ——先前這條路徑直接呼叫 facade，任何搬到 facade 外層的中介層都會被跳過。
        /// </summary>
        private static async Task<T> FrameworkPathAsync<T>(
            IChatClient client,
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options,
            CancellationToken cancellationToken)
        {
            ChatOptions effective = (options ?? new RimLLMChatOptions()).Clone();
            if (effective.AdditionalProperties == null)
            {
                effective.AdditionalProperties = new AdditionalPropertiesDictionary();
            }
            effective.AdditionalProperties[RimLLMChatOptions.ResponseTypeKey] = typeof(T);

            ChatResponse response = await client
                .GetResponseAsync(messages, effective, cancellationToken)
                .ConfigureAwait(false);

            string raw = response?.Text ?? string.Empty;
            return RimLLMProvider.TryGetManager(out RimLLMManager manager)
                ? manager.DeserializeStructured<T>(raw, manager.Settings)
                : RimLLMManager.DeserializeAndValidate<T>(raw);
        }

        private static async Task<T> SimplifiedPathAsync<T>(
            IChatClient client,
            IEnumerable<ChatMessage> messages,
            RimLLMChatOptions options,
            CancellationToken cancellationToken)
        {
            ChatOptions effective = options ?? new RimLLMChatOptions();
            // schema 的快取由 RimLLMSchemaBuilder 統一負責，此處不再另建一份。
            // 這條路徑接受任意 IChatClient，無從得知目標供應商，因此一律以 OpenAI 方言送出；
            // 走 RimLLM facade 的請求則會依 provider capability 選擇正確方言。
            string schemaJson = RimLLMSchemaBuilder.BuildJson(typeof(T), RimLLMSchemaProfile.OpenAI);
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
#pragma warning restore S101, S2342
}
using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace RimLLM_Framework.Manager
{
#region pragma 抑制說明
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
#endregion

    /// <summary>
    /// 把 <see cref="ChatResponse"/> 深層複製成一份與來源隔離的實例。
    /// </summary>
    /// <remarks>
    /// 回應快取的寫入與讀出都必須隔離實例：<c>ChatResponse.Messages</c> 是可寫清單，
    /// 內層訊息與 <c>AIContent</c> 也是可變物件。若快取持有的是呼叫端拿到的同一份實例，
    /// 呼叫端往 Messages 追加訊息、改 Usage 或 AdditionalProperties，就會污染後續所有命中。
    /// 隔離範圍（保證）：
    /// <list type="bullet">
    /// <item>Messages／Contents／Usage／Annotations 清單與字典容器一律重建；</item>
    /// <item><c>AdditionalProperties</c>、工具參數、工具結果中的巢狀容器
    /// （<c>IDictionary&lt;string,object&gt;</c>、非泛型 <c>IDictionary</c>、
    /// 陣列、<c>List&lt;T&gt;</c>、其他 <c>IList</c>）遞迴重建；</item>
    /// <item><c>DataContent</c> 以 data URI 重建（含位元組）、<c>UriContent</c> 重建，
    /// <c>TextContent</c>／<c>TextReasoningContent</c>／<c>FunctionCallContent</c>／
    /// <c>FunctionResultContent</c>／<c>UsageContent</c> 依型別重建；</item>
    /// </list>
    /// 不保證（刻意共用）：
    /// <list type="bullet">
    /// <item>未知的自訂 <c>AIContent</c> 子類別：無法可靠重建，原樣保留；</item>
    /// <item><c>AIContent.RawRepresentation</c> 與 <c>ChatResponse.RawRepresentation</c>：
    /// 那是 provider 的私有物件，框架不讀、呼叫端也不該改；</item>
    /// <item>容器值中的任意自訂參考型別（如自訂 POCO）：<c>CopyValue</c> 只重建上述
    /// 容器形狀，其餘參考型別原樣保留，呼叫端若修改其內部仍可能影響快取；</item>
    /// <item><c>Annotations</c> 元素與 <c>Exception</c> 實例：唯讀資料，原樣保留。</item>
    /// </list>
    /// </remarks>
    internal static class RimLLMResponseDeepCopy
    {
        /// <summary>複製整個回應。來源為 null 時回傳 null。</summary>
        public static ChatResponse Copy(ChatResponse source)
        {
            if (source == null) return null;

            var messages = new List<ChatMessage>(source.Messages?.Count ?? 0);
            if (source.Messages != null)
            {
                foreach (ChatMessage message in source.Messages)
                {
                    messages.Add(Copy(message));
                }
            }

            return new ChatResponse(messages)
            {
                ResponseId = source.ResponseId,
                ConversationId = source.ConversationId,
                ModelId = source.ModelId,
                CreatedAt = source.CreatedAt,
                FinishReason = source.FinishReason,
                Usage = Copy(source.Usage),
                AdditionalProperties = Copy(source.AdditionalProperties),
                ContinuationToken = source.ContinuationToken,
                RawRepresentation = source.RawRepresentation
            };
        }

        /// <summary>複製串流 update，供快取重播以外的路徑也需要獨立實例時使用。</summary>
        public static ChatResponseUpdate Copy(ChatResponseUpdate source)
        {
            if (source == null) return null;

            return new ChatResponseUpdate(source.Role, CopyContents(source.Contents))
            {
                AuthorName = source.AuthorName,
                MessageId = source.MessageId,
                ResponseId = source.ResponseId,
                ConversationId = source.ConversationId,
                ModelId = source.ModelId,
                CreatedAt = source.CreatedAt,
                FinishReason = source.FinishReason,
                AdditionalProperties = Copy(source.AdditionalProperties),
                ContinuationToken = source.ContinuationToken,
                RawRepresentation = source.RawRepresentation
            };
        }

        private static ChatMessage Copy(ChatMessage source)
        {
            if (source == null) return null;

            return new ChatMessage(source.Role, CopyContents(source.Contents))
            {
                AuthorName = source.AuthorName,
                CreatedAt = source.CreatedAt,
                MessageId = source.MessageId,
                AdditionalProperties = Copy(source.AdditionalProperties),
                RawRepresentation = source.RawRepresentation
            };
        }

        private static IList<AIContent> CopyContents(IList<AIContent> contents)
        {
            if (contents == null) return null;

            var copy = new List<AIContent>(contents.Count);
            foreach (AIContent content in contents)
            {
                copy.Add(CopyContent(content));
            }
            return copy;
        }

        /// <summary>
        /// 依實際型別重建 <see cref="AIContent"/>。未知的子類別退回原實例——
        /// 框架只保證自己認得的內容型別可被安全隔離，自訂型別無法可靠重建。
        /// </summary>
        private static AIContent CopyContent(AIContent source)
        {
            if (source == null) return null;

            switch (source)
            {
                case TextContent text:
                    return CopyBase(new TextContent(text.Text), text);
                case TextReasoningContent reasoning:
                    return CopyBase(new TextReasoningContent(reasoning.Text)
                    {
                        ProtectedData = reasoning.ProtectedData
                    }, reasoning);
                case FunctionCallContent call:
                {
                    var arguments = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (call.Arguments != null)
                    {
                        foreach (KeyValuePair<string, object> pair in call.Arguments)
                        {
                            arguments[pair.Key] = CopyValue(pair.Value);
                        }
                    }
                    return CopyBase(new FunctionCallContent(call.CallId, call.Name, arguments)
                    {
                        Exception = call.Exception,
                        InformationalOnly = call.InformationalOnly
                    }, call);
                }
                case FunctionResultContent result:
                    return CopyBase(new FunctionResultContent(result.CallId, CopyValue(result.Result))
                    {
                        Exception = result.Exception
                    }, result);
                case UsageContent usage:
                    return CopyBase(new UsageContent(Copy(usage.Details)), usage);
                case DataContent data:
                {
                    // DataContent.Uri 恆為含位元組的 data URI，以它重建即複製了位元組本體。
                    try
                    {
                        var rebuilt = new DataContent(data.Uri, data.MediaType)
                        {
                            Name = data.Name
                        };
                        return CopyBase(rebuilt, data);
                    }
                    catch
                    {
                        return source;
                    }
                }
                case UriContent uri:
                    return CopyBase(new UriContent(uri.Uri, uri.MediaType), uri);
                default:
                    // 無法安全重建的自訂內容型別：原樣保留。
                    // 這類內容通常由 provider 產生且不具備可寫表面，呼叫端不會去改它。
                    return source;
            }
        }

        /// <summary>把基底類別的可寫成員（Annotations／AdditionalProperties）複製到重建的實例。</summary>
        private static AIContent CopyBase(AIContent target, AIContent source)
        {
            target.Annotations = CopyAnnotations(source.Annotations);
            target.AdditionalProperties = Copy(source.AdditionalProperties);
            return target;
        }

        /// <summary>Annotations 複製成新清單；元素本身為唯讀資料，原樣保留。</summary>
        private static IList<AIAnnotation> CopyAnnotations(IList<AIAnnotation> annotations)
        {
            if (annotations == null) return null;

            var copy = new List<AIAnnotation>(annotations.Count);
            foreach (AIAnnotation annotation in annotations)
            {
                copy.Add(annotation);
            }
            return copy;
        }

        /// <summary>複製 UsageDetails：所有可寫計數欄位逐一搬移。</summary>
        public static UsageDetails Copy(UsageDetails source)
        {
            if (source == null) return null;

            return new UsageDetails
            {
                InputTokenCount = source.InputTokenCount,
                OutputTokenCount = source.OutputTokenCount,
                TotalTokenCount = source.TotalTokenCount,
                CachedInputTokenCount = source.CachedInputTokenCount,
                ReasoningTokenCount = source.ReasoningTokenCount,
                InputAudioTokenCount = source.InputAudioTokenCount,
                InputTextTokenCount = source.InputTextTokenCount,
                OutputAudioTokenCount = source.OutputAudioTokenCount,
                OutputTextTokenCount = source.OutputTextTokenCount,
                AdditionalCounts = source.AdditionalCounts != null
                    ? new AdditionalPropertiesDictionary<long>(source.AdditionalCounts)
                    : null
            };
        }

        /// <summary>AdditionalProperties 複製成新字典，巢狀容器以 <see cref="CopyValue"/> 遞迴重建。</summary>
        public static AdditionalPropertiesDictionary Copy(AdditionalPropertiesDictionary source)
        {
            if (source == null) return null;

            var copy = new AdditionalPropertiesDictionary();
            foreach (KeyValuePair<string, object> pair in source)
            {
                copy[pair.Key] = CopyValue(pair.Value);
            }
            return copy;
        }

        /// <summary>
        /// 複製一個任意值。不可變量（null、字串、實值型別）直接共用；
        /// 常見可變容器（字串鍵字典、非泛型字典、陣列、<c>List&lt;T&gt;</c>、其他 <c>IList</c>、
        /// <c>ReadOnlyMemory&lt;byte&gt;</c>）遞迴重建；其餘自訂參考型別無法可靠重建，原樣保留。
        /// </summary>
        private static object CopyValue(object value)
        {
            if (value == null) return null;
            if (value is string) return value;

            Type valueType = value.GetType();
            if (valueType.IsValueType)
            {
                // 實值型別直接共用（內含的陣列參照除外，見類別註解的不保證事項）。
                return value;
            }

            if (value is IDictionary<string, object> stringDict)
            {
                var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in stringDict)
                {
                    copy[pair.Key] = CopyValue(pair.Value);
                }
                return copy;
            }

            if (value is System.Collections.IDictionary dictionary)
            {
                bool allStringKeys = true;
                foreach (object key in dictionary.Keys)
                {
                    if (!(key is string))
                    {
                        allStringKeys = false;
                        break;
                    }
                }
                if (allStringKeys)
                {
                    var copy = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (System.Collections.DictionaryEntry entry in dictionary)
                    {
                        copy[(string)entry.Key] = CopyValue(entry.Value);
                    }
                    return copy;
                }
                var objectCopy = new Dictionary<object, object>();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    objectCopy[entry.Key] = CopyValue(entry.Value);
                }
                return objectCopy;
            }

            if (value is Array array)
            {
                var copy = (Array)Array.CreateInstance(valueType.GetElementType(), array.Length);
                for (int i = 0; i < array.Length; i++)
                {
                    copy.SetValue(CopyValue(array.GetValue(i)), i);
                }
                return copy;
            }

            if (value is System.Collections.IList list)
            {
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var copy = (System.Collections.IList)Activator.CreateInstance(valueType);
                    foreach (object item in list)
                    {
                        copy.Add(CopyValue(item));
                    }
                    return copy;
                }
                var objectItems = new List<object>(list.Count);
                foreach (object item in list)
                {
                    objectItems.Add(CopyValue(item));
                }
                return objectItems;
            }

            return value;
        }
    }
#pragma warning restore S101
}

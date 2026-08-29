using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework
{
#pragma warning disable S101, S2342 // reason: RimLLM 為品牌縮寫，公開 API 重命名會破壞下游 Mod，維持現狀
#pragma warning disable S3881 // reason: IEmbeddingGenerator 門面，Dispose 僅標記已釋放無實質資源，現有模式已足夠
    /// <summary>
    /// 綁定單一 Mod 的 IEmbeddingGenerator facade。內部接到既有 RimLLMEmbeddingService
    /// （線上供應商 + 防濫用）。透過 RimLLMProvider.CreateEmbeddingGenerator(modId) 取得。
    /// </summary>
    internal class RimLLMEmbeddingClient : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly RimLLMManager _manager;
        private readonly string _modId;
        private bool _disposed;

        internal RimLLMEmbeddingClient(RimLLMManager manager, string modId)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));

            _manager.CheckAntiAbuseForMod(_modId);
            IReadOnlyList<float[]> vectors = await _manager.EmbeddingService
                .ComputeEmbeddingsAsync(values, cancellationToken)
                .ConfigureAwait(false);

            var embeddings = new List<Embedding<float>>(vectors.Count);
            foreach (float[] vector in vectors)
            {
                embeddings.Add(new Embedding<float>(vector));
            }
            return new GeneratedEmbeddings<Embedding<float>>(embeddings);
        }

        public object GetService(Type serviceType, object serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                // 目前無託管資源需釋放，保留供未來擴充。
            }
            _disposed = true;
        }
    }
#pragma warning restore S3881
#pragma warning restore S101, S2342
}
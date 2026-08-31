using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 管理 API 供應商的健康帳本（Health Ledger），
    /// 包含呼叫成果記錄、連續失敗退避熔斷、單次失敗暫時冷卻與延遲統計。
    /// </summary>
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    public class RimLLMHealthLedger
    {
        private sealed class ProviderHealthRecord
        {
            public int ContinuousFailures;
            public DateTime CooldownUntil = DateTime.MinValue;
            public readonly List<long> Latencies = new List<long>();
        }

        private readonly ConcurrentDictionary<string, ProviderHealthRecord> _ledger =
            new ConcurrentDictionary<string, ProviderHealthRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <summary>
        /// 記錄一次成功的呼叫，重設連續失敗次數、解除冷卻並更新延遲。
        /// </summary>
        public void RecordSuccess(string providerId, long elapsedMs = 0)
        {
            if (string.IsNullOrEmpty(providerId)) return;

            var record = _ledger.GetOrAdd(providerId, _ => new ProviderHealthRecord());
            lock (_lock)
            {
                record.ContinuousFailures = 0;
                record.CooldownUntil = DateTime.MinValue;
                if (elapsedMs > 0)
                {
                    record.Latencies.Add(elapsedMs);
                    if (record.Latencies.Count > 5)
                    {
                        record.Latencies.RemoveAt(0);
                    }
                }
            }
        }

        /// <summary>
        /// 記錄一次失敗的呼叫。
        /// 若為可重試故障，連續失敗達 3 次以上則觸發指數退避熔斷（60s, 120s, 240s, 480s, 960s）；
        /// 連續失敗 1~2 次給予 60 秒暫時冷卻以利備援轉移。
        /// </summary>
        public void RecordFailure(string providerId, bool isRetryable = true)
        {
            if (string.IsNullOrEmpty(providerId) || !isRetryable) return;

            var record = _ledger.GetOrAdd(providerId, _ => new ProviderHealthRecord());
            lock (_lock)
            {
                record.ContinuousFailures++;
                double cooldownSeconds;
                if (record.ContinuousFailures >= 3)
                {
                    int power = record.ContinuousFailures - 3;
                    cooldownSeconds = 60.0 * Math.Pow(2, Math.Min(power, 4));
                    RimLLMLog.Warning($"[RimLLM] Provider {providerId} has failed {record.ContinuousFailures} times continuously. Circuit cooldown set for {cooldownSeconds} seconds.");
                }
                else
                {
                    cooldownSeconds = 60.0;
                }

                DateTime newCooldown = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                if (newCooldown > record.CooldownUntil)
                {
                    record.CooldownUntil = newCooldown;
                }
            }
        }

        /// <summary>
        /// 檢查指定供應商目前是否處於冷卻或熔斷狀態中。
        /// </summary>
        public bool IsInCooldown(string providerId, out DateTime cooldownUntil, out int continuousFailures)
        {
            cooldownUntil = DateTime.MinValue;
            continuousFailures = 0;

            if (!string.IsNullOrEmpty(providerId) && _ledger.TryGetValue(providerId, out var record))
            {
                lock (_lock)
                {
                    cooldownUntil = record.CooldownUntil;
                    continuousFailures = record.ContinuousFailures;
                    if (record.CooldownUntil > DateTime.UtcNow)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 簡化版冷卻檢查。
        /// </summary>
        public bool IsInCooldown(string providerId)
        {
            return IsInCooldown(providerId, out _, out _);
        }

        /// <summary>
        /// 檢查候選清單中的項目是否全部都在冷卻中（若是，則允許破例放行以免阻斷所有呼叫）。
        /// </summary>
        public bool AreAllInCooldown<T>(IEnumerable<T> candidates, Func<T, string> providerIdSelector)
        {
            if (candidates == null || providerIdSelector == null) return false;

            bool anyCandidate = false;
            foreach (var candidate in candidates)
            {
                string providerId = providerIdSelector(candidate);
                if (string.IsNullOrEmpty(providerId)) continue;
                anyCandidate = true;
                if (!IsInCooldown(providerId))
                {
                    return false;
                }
            }
            return anyCandidate;
        }

        /// <summary>
        /// 取得供應商的平均延遲（毫秒），無記錄時回傳 0。
        /// </summary>
        public float GetAverageLatency(string providerId)
        {
            if (!string.IsNullOrEmpty(providerId) && _ledger.TryGetValue(providerId, out var record))
            {
                lock (_lock)
                {
                    if (record.Latencies.Count > 0)
                    {
                        long sum = 0;
                        foreach (long lat in record.Latencies) sum += lat;
                        return (float)sum / record.Latencies.Count;
                    }
                }
            }
            return 0f;
        }

        /// <summary>
        /// 清除所有健康與冷卻記錄。
        /// </summary>
        public void Clear()
        {
            _ledger.Clear();
        }
    }
}

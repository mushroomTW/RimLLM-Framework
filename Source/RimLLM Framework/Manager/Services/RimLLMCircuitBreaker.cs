using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Manager
{
    /// <summary>
    /// 管理 API 供應商的 Circuit Breaker 健康狀態與熔斷冷卻機制。
    /// 當供應商連續失敗達一定次數時，會使該供應商進入冷卻狀態，暫時跳過輪詢。
    /// </summary>
    public class RimLLMCircuitBreaker
    {
        private class ProviderHealth
        {
            public int ContinuousFailures { get; set; }
            public DateTime CooldownEndTime { get; set; } = DateTime.MinValue;
        }

        private readonly ConcurrentDictionary<string, ProviderHealth> _providerHealth = 
            new ConcurrentDictionary<string, ProviderHealth>(StringComparer.OrdinalIgnoreCase);
        private readonly object _healthLock = new object();

        /// <summary>
        /// 檢查供應商是否正處於冷卻熔斷狀態中。
        /// </summary>
        public bool IsCooldown(string providerId, out DateTime cooldownEndTime, out int continuousFailures)
        {
            cooldownEndTime = DateTime.MinValue;
            continuousFailures = 0;

            var health = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
            lock (_healthLock)
            {
                if (health.CooldownEndTime > DateTime.UtcNow)
                {
                    cooldownEndTime = health.CooldownEndTime;
                    continuousFailures = health.ContinuousFailures;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 記錄一次調用成功，清除連續失敗記錄與冷卻時間。
        /// </summary>
        public void RecordSuccess(string providerId)
        {
            var health = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
            lock (_healthLock)
            {
                health.ContinuousFailures = 0;
                health.CooldownEndTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 記錄一次調用失敗。若連續失敗達到 3 次或以上，將依指數形式退避計算冷卻時間。
        /// </summary>
        public void RecordFailure(string providerId)
        {
            var health = _providerHealth.GetOrAdd(providerId, _ => new ProviderHealth());
            lock (_healthLock)
            {
                health.ContinuousFailures++;
                if (health.ContinuousFailures >= 3)
                {
                    int power = health.ContinuousFailures - 3;
                    double cooldownSeconds = 60.0 * Math.Pow(2, Math.Min(power, 4));
                    health.CooldownEndTime = DateTime.UtcNow.AddSeconds(cooldownSeconds);
                    RimLLMLog.Warning($"[RimLLM] Provider {providerId} has failed {health.ContinuousFailures} times continuously. Cooldown set for {cooldownSeconds} seconds.");
                }
            }
        }

        /// <summary>
        /// 判斷 Fallback Chain 中所有符合資格的供應商是否全部都在冷卻狀態中。
        /// 資格判斷（已註冊、啟用、金鑰）由呼叫端透過 isEligibleProvider 提供，避免特例邏輯散落。
        /// </summary>
        public bool AreAllEligibleProvidersInCooldown(
            List<string> fallbackChain,
            Func<string, bool> isEligibleProvider)
        {
            foreach (string entry in fallbackChain)
            {
                if (!ResolveFallbackEntry(entry, out string providerId)) continue;
                if (!isEligibleProvider(providerId)) continue;

                if (!IsCooldown(providerId, out _, out _))
                {
                    return false; // 有一個可用且不在冷卻中
                }
            }
            return true;
        }

        private bool ResolveFallbackEntry(string entry, out string providerId)
        {
            providerId = entry;
            if (string.IsNullOrEmpty(entry))
            {
                return false;
            }

            int colonIndex = entry.IndexOf(':');
            if (colonIndex > 0)
            {
                providerId = entry.Substring(0, colonIndex);
            }
            return true;
        }
    }
}

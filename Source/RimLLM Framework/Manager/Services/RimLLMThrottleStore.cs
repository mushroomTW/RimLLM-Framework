using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 各個 Mod 的請求時間視窗與冷卻狀態。
    /// </summary>
    /// <remarks>
    /// 由 manager 持有單一實例，注入每個 per-mod 的防濫用中介層。
    /// 這個共用是必要的：若改成每個 client 各持一份，同一個 Mod 只要多呼叫幾次
    /// CreateChatClient 就能繞過節流，而 ClearCooldowns 也只會清掉其中一份。
    /// </remarks>
    internal sealed class RimLLMThrottleStore
    {
        private readonly IRimLLMSettings _settings;

        private readonly ConcurrentDictionary<string, List<DateTime>> _requestTimestamps =
            new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _coolDownUntil =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public RimLLMThrottleStore(IRimLLMSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 檢查並記錄一次請求。超出視窗上限時讓該 Mod 進入冷卻並擲出 <see cref="LLMError.RateLimit"/>。
        /// </summary>
        public void CheckAntiAbuse(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            DateTime now = DateTime.UtcNow;
            if (_coolDownUntil.TryGetValue(modId, out DateTime cdTime) && now < cdTime)
            {
                throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' is in anti-abuse cooldown until {cdTime.ToLocalTime()}.");
            }

            var list = _requestTimestamps.GetOrAdd(modId, _ => new List<DateTime>());
            lock (list)
            {
                DateTime limit = now.AddSeconds(-_settings.ThrottlingWindowSeconds);
                list.RemoveAll(t => t < limit);
                list.Add(now);

                if (list.Count > _settings.MaxRequestsPerWindow)
                {
                    DateTime cdUntil = now.AddSeconds(_settings.CoolDownDurationSeconds);
                    _coolDownUntil[modId] = cdUntil;
                    RimLLMLog.Warning($"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                    throw new RimLLMException(LLMError.RateLimit, $"[RimLLM] Mod '{modId}' triggered anti-abuse throttling limit. Cooling down until {cdUntil.ToLocalTime()}.");
                }
            }
        }

        public void ClearCooldowns()
        {
            _requestTimestamps.Clear();
            _coolDownUntil.Clear();
        }
    }
#pragma warning restore S101
}

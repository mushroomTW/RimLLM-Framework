using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Tests;

/// <summary>
/// 性質測試：以固定種子產生隨機操作序列，與簡化的參考模型逐步比對。
/// 單元測試只涵蓋手寫的幾條路徑；預算、節流與熔斷的錯誤通常出現在操作交錯的組合裡。
/// 失敗訊息帶種子與步數，可用同一個種子重現。
/// </summary>
[TestFixture]
public class StatefulPropertyTests
{
    private const int Seeds = 200;
    private const int StepsPerSeed = 60;

    [Test]
    public void BudgetReservationsMatchModel()
    {
        for (int seed = 0; seed < Seeds; seed++)
        {
            var random = new Random(seed);
            long limit = random.Next(100, 10_000);
            var settings = new MockSettings { DailyTokenBudgetLimit = limit, BudgetPolicy = 0 };
            var tracker = new RimLLMUsageTracker(settings);
            tracker.CheckDailyReset();
            settings.DailyAccumulatedTokens = 0;

            long accumulated = 0;
            var live = new List<(RimLLMUsageTracker.BudgetReservation Reservation, long Amount)>();
            var disposed = new List<RimLLMUsageTracker.BudgetReservation>();

            for (int step = 0; step < StepsPerSeed; step++)
            {
                string where = string.Create(CultureInfo.InvariantCulture, $"seed {seed} step {step}");
                switch (random.Next(5))
                {
                    case 0:
                    case 1:
                    {
                        long estimate = random.Next(0, (int)(limit * 3 / 2));
                        bool over = accumulated >= limit;
                        bool contended = !over && live.Sum(r => r.Amount) >= limit - accumulated;

                        bool ok = tracker.TryReserveBudget(estimate, out var reservation, out bool actualContended);

                        Assert.That(ok, Is.EqualTo(!over && !contended), where + "：預留結果");
                        Assert.That(actualContended, Is.EqualTo(contended), where + "：contended");
                        if (ok)
                        {
                            live.Add((reservation, Math.Min(Math.Max(1, estimate), limit)));
                        }
                        break;
                    }
                    case 2 when live.Count > 0:
                    {
                        int index = random.Next(live.Count);
                        live[index].Reservation.Dispose();
                        disposed.Add(live[index].Reservation);
                        live.RemoveAt(index);
                        break;
                    }
                    case 3 when disposed.Count > 0:
                        // 重複 Dispose 不得再釋放一次額度；要在仍有在途預留時做，多釋放才看得出來。
                        disposed[random.Next(disposed.Count)].Dispose();
                        break;
                    default:
                    {
                        int prompt = random.Next(0, (int)(limit / 3) + 1);
                        int completion = random.Next(0, (int)(limit / 3) + 1);
                        tracker.RecordUsage("unknown", "model", prompt, completion);
                        accumulated += prompt + completion;
                        Assert.That(settings.DailyAccumulatedTokens, Is.EqualTo(accumulated), where + "：累計用量");
                        break;
                    }
                }
            }
        }
    }

    [Test]
    public void HealthLedgerCooldownMatchesModel()
    {
        const string Target = "provider/model";
        for (int seed = 0; seed < Seeds; seed++)
        {
            var random = new Random(seed);
            var ledger = new RimLLMHealthLedger();
            int failures = 0;
            DateTime until = DateTime.MinValue;
            var latencies = new List<long>();

            for (int step = 0; step < StepsPerSeed; step++)
            {
                string where = string.Create(CultureInfo.InvariantCulture, $"seed {seed} step {step}");
                int op = random.Next(3);
                if (op == 0)
                {
                    long elapsed = random.Next(0, 500);
                    ledger.RecordSuccess(Target, elapsed);
                    failures = 0;
                    until = DateTime.MinValue;
                    if (elapsed > 0)
                    {
                        latencies.Add(elapsed);
                        if (latencies.Count > 5)
                        {
                            latencies.RemoveAt(0);
                        }
                    }

                    Assert.That(ledger.IsInCooldown(Target), Is.False, where + "：成功後應解除冷卻");
                    float expectedLatency = latencies.Count == 0 ? 0f : (float)latencies.Sum() / latencies.Count;
                    Assert.That(ledger.GetAverageLatency(Target), Is.EqualTo(expectedLatency).Within(0.001f), where + "：平均延遲");
                    continue;
                }

                bool retryable = op == 1;
                DateTime before = DateTime.UtcNow;
                ledger.RecordFailure(Target, retryable);
                DateTime after = DateTime.UtcNow;

                ledger.IsInCooldown(Target, before, out DateTime actualUntil, out int actualFailures);
                if (!retryable)
                {
                    Assert.That(actualFailures, Is.EqualTo(failures), where + "：不可重試的失敗不計入");
                    Assert.That(actualUntil, Is.EqualTo(until), where + "：不可重試的失敗不改冷卻");
                    continue;
                }

                failures++;
                double seconds = failures >= 3 ? 60.0 * Math.Pow(2, Math.Min(failures - 3, 4)) : 15.0;
                Assert.That(actualFailures, Is.EqualTo(failures), where + "：連續失敗次數");
                Assert.That(actualUntil, Is.GreaterThanOrEqualTo(before.AddSeconds(seconds)), where + "：冷卻下限");
                Assert.That(actualUntil, Is.LessThanOrEqualTo(Max(until, after.AddSeconds(seconds))), where + "：冷卻上限");
                Assert.That(actualUntil, Is.GreaterThanOrEqualTo(until), where + "：冷卻不得縮短");
                until = actualUntil;
            }
        }
    }

    [Test]
    public void ThrottleStoreMatchesModel()
    {
        string[] mods = ["mod.a", "mod.b", "mod.c"];
        for (int seed = 0; seed < Seeds; seed++)
        {
            var random = new Random(seed);
            int perMod = random.Next(1, 6);
            var store = new RimLLMThrottleStore(new MockSettings
            {
                MaxRequestsPerWindow = perMod,
                ThrottlingWindowSeconds = 3600,
                CoolDownDurationSeconds = 3600,
            });
            var modCounts = mods.ToDictionary(m => m, _ => 0);
            var modCooling = new HashSet<string>();
            int globalCount = 0;
            bool globalCooling = false;

            for (int step = 0; step < StepsPerSeed; step++)
            {
                string where = string.Create(CultureInfo.InvariantCulture, $"seed {seed} step {step}");
                string mod = mods[random.Next(mods.Length)];
                bool count = random.Next(3) != 0;

                bool expectThrow;
                if (modCooling.Contains(mod) || globalCooling)
                {
                    expectThrow = true;
                }
                else if (globalCount >= perMod * 10)
                {
                    globalCooling = true;
                    expectThrow = true;
                }
                else
                {
                    globalCount++;
                    expectThrow = false;
                    if (count && ++modCounts[mod] > perMod)
                    {
                        modCooling.Add(mod);
                        expectThrow = true;
                    }
                }

                RimLLMException thrown = null;
                try
                {
                    store.CheckAntiAbuse(mod, count);
                }
                catch (RimLLMException ex)
                {
                    thrown = ex;
                }

                Assert.That(thrown != null, Is.EqualTo(expectThrow), where + "：" + mod + " count=" + count);
                if (thrown != null)
                {
                    Assert.That(thrown.Error, Is.EqualTo(LLMError.RateLimit), where);
                }
            }
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

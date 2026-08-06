using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Verse;
using RimLLM_Framework.Core;
using RimLLM_Framework.Manager;

namespace RimLLM_Framework.Mod
{
    /// <summary>
    /// 遙測資料儲存（對話測試歷史、請求日誌、Token 用量統計）。
    /// 與 ModSettings 設定本體分離，獨立存放於 Config 資料夾的 JSON 檔案，
    /// 避免高頻變動的遙測資料讓設定 XML 膨脹並拖慢設定讀寫。
    /// 對話歷史含使用者輸入與模型完整回覆，因此以 AES 加密後才落地。
    /// </summary>
    public class RimLLMTelemetryStore
    {
        private const string FileName = "RimLLM_Telemetry.json";
        private const int MaxChatHistoryEntries = 100;

        private readonly object _ioLock = new object();

        /// <summary>
        /// 檔案路徑解析器。抽為可替換的委派，讓單元測試能指向暫存目錄，
        /// 不必依賴 Verse 的 GenFilePaths。
        /// </summary>
        internal static Func<string> FilePathResolver =
            () => Path.Combine(GenFilePaths.ConfigFolderPath, FileName);

        public List<string> ChatHistory { get; set; } = new List<string>();
        public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = new List<RimLLMManager.RequestLogEntry>();
        public long TotalPromptTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public float TotalEstimatedCost { get; set; }
        public float DailyAccumulatedCost { get; set; }
        public string DailyBudgetResetDate { get; set; } = "";

        /// <summary>
        /// 磁碟上是否已存在遙測檔案。用於判斷是否需要從舊版設定 XML 遷移。
        /// </summary>
        public bool LoadedFromDisk { get; private set; }

        /// <summary>
        /// 是否有尚未寫入磁碟的變更。用量統計採節流寫入，
        /// 關閉遊戲時需依此判斷是否強制 flush，避免遺失最後一段用量。
        /// </summary>
        public bool IsDirty { get; private set; }

        private class TelemetryDto
        {
            /// <summary>加密後的對話歷史（JSON 陣列序列化後再以 AES 加密）。</summary>
            public string EncryptedChatHistory;

            /// <summary>舊版的明文對話歷史，僅供一次性遷移讀取，不再寫入。</summary>
            public List<string> ChatHistory;

            public List<RimLLMManager.RequestLogEntry> RequestLogs;
            public long TotalPromptTokens;
            public long TotalCompletionTokens;
            public float TotalEstimatedCost;
            public float DailyAccumulatedCost;
            public string DailyBudgetResetDate;
        }

        private static string GetFilePath()
        {
            return FilePathResolver();
        }

        /// <summary>
        /// 標記有未寫入的變更（供節流寫入路徑呼叫）。
        /// </summary>
        public void MarkDirty()
        {
            IsDirty = true;
        }

        /// <summary>
        /// 從磁碟載入遙測資料。檔案不存在或格式錯誤時保留預設空值；
        /// 主檔損毀時會嘗試從 .bak 備份還原。
        /// </summary>
        public void Load()
        {
            lock (_ioLock)
            {
                string path;
                try
                {
                    path = GetFilePath();
                }
                catch (Exception ex)
                {
                    // 非 Unity 環境（單元測試、headless 反射執行）取不到 Config 路徑。
                    RimLLMLog.Warning($"[RimLLM] 無法解析遙測檔案路徑，略過載入: {ex.Message}");
                    return;
                }

                if (TryLoadFrom(path, out bool needsRewrite))
                {
                    LoadedFromDisk = true;
                    // 讀到的是舊版明文歷史，標記為待重寫以完成加密遷移。
                    if (needsRewrite) IsDirty = true;
                    return;
                }

                string backupPath = path + ".bak";
                if (TryLoadFrom(backupPath, out _))
                {
                    RimLLMLog.Warning("[RimLLM] 遙測主檔無法解析，已改由備份檔還原。");
                    LoadedFromDisk = true;
                    IsDirty = true;
                }
            }
        }

        private bool TryLoadFrom(string path, out bool needsRewrite)
        {
            needsRewrite = false;
            try
            {
                if (!File.Exists(path)) return false;

                var dto = JsonConvert.DeserializeObject<TelemetryDto>(File.ReadAllText(path));
                if (dto == null) return false;

                ChatHistory = ReadChatHistory(dto, out needsRewrite);
                TrimChatHistory();
                RequestLogs = dto.RequestLogs ?? new List<RimLLMManager.RequestLogEntry>();
                TotalPromptTokens = dto.TotalPromptTokens;
                TotalCompletionTokens = dto.TotalCompletionTokens;
                TotalEstimatedCost = dto.TotalEstimatedCost;
                DailyAccumulatedCost = dto.DailyAccumulatedCost;
                DailyBudgetResetDate = dto.DailyBudgetResetDate ?? "";
                return true;
            }
            catch (Exception ex)
            {
                RimLLMLog.Warning($"[RimLLM] 載入遙測資料失敗 ({Path.GetFileName(path)}): {ex.Message}");
                return false;
            }
        }

        private static List<string> ReadChatHistory(TelemetryDto dto, out bool needsRewrite)
        {
            needsRewrite = false;

            if (!string.IsNullOrEmpty(dto.EncryptedChatHistory))
            {
                string plain = EncryptionUtility.Decrypt(dto.EncryptedChatHistory);
                if (plain == null)
                {
                    // 換裝置導致解不開：以空歷史起始即可，不得讓整份遙測載入失敗。
                    RimLLMLog.Warning("[RimLLM] 對話歷史無法解密（可能已更換裝置），將以空白歷史起始。");
                    return new List<string>();
                }

                try
                {
                    return JsonConvert.DeserializeObject<List<string>>(plain) ?? new List<string>();
                }
                catch
                {
                    return new List<string>();
                }
            }

            // 舊版明文欄位：讀入後標記待重寫，下次 Save 即完成加密遷移。
            if (dto.ChatHistory != null && dto.ChatHistory.Count > 0)
            {
                needsRewrite = true;
                return dto.ChatHistory;
            }

            return new List<string>();
        }

        /// <summary>
        /// 將遙測資料寫入磁碟。
        /// 採「暫存檔 → 原子替換」避免程序中斷造成檔案截斷，並保留上一份有效檔為 .bak。
        /// </summary>
        public void Save()
        {
            lock (_ioLock)
            {
                string path;
                try
                {
                    path = GetFilePath();
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 無法解析遙測檔案路徑，略過寫入: {ex.Message}");
                    return;
                }

                string tempPath = path + ".tmp";
                string backupPath = path + ".bak";

                try
                {
                    TrimChatHistory();

                    string encryptedHistory = null;
                    if (ChatHistory != null && ChatHistory.Count > 0)
                    {
                        encryptedHistory = EncryptionUtility.Encrypt(
                            JsonConvert.SerializeObject(ChatHistory, Formatting.None));
                    }

                    var dto = new TelemetryDto
                    {
                        EncryptedChatHistory = encryptedHistory,
                        // 明文欄位明確寫 null，讓舊版殘留的明文歷史在首次存檔後即被清除。
                        ChatHistory = null,
                        RequestLogs = RequestLogs,
                        TotalPromptTokens = TotalPromptTokens,
                        TotalCompletionTokens = TotalCompletionTokens,
                        TotalEstimatedCost = TotalEstimatedCost,
                        DailyAccumulatedCost = DailyAccumulatedCost,
                        DailyBudgetResetDate = DailyBudgetResetDate
                    };

                    File.WriteAllText(tempPath, JsonConvert.SerializeObject(dto, Formatting.None));
                    ReplaceAtomically(tempPath, path, backupPath);

                    LoadedFromDisk = true;
                    IsDirty = false;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 寫入遙測資料失敗: {ex.Message}");
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }
        }

        private static void ReplaceAtomically(string tempPath, string path, string backupPath)
        {
            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            try
            {
                File.Replace(tempPath, path, backupPath);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is IOException)
            {
                // 部分檔案系統不支援 File.Replace，退回「先備份再覆寫」。
                try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                try { File.Move(path, backupPath); } catch { }
                File.Move(tempPath, path);
            }
        }

        private void TrimChatHistory()
        {
            // 限制大小在 100 條內，防遙測 JSON 無限膨脹
            if (ChatHistory != null && ChatHistory.Count > MaxChatHistoryEntries)
            {
                ChatHistory.RemoveRange(0, ChatHistory.Count - MaxChatHistoryEntries);
            }
        }
    }
}

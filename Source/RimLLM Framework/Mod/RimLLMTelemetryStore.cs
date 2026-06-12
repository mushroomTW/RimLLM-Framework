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
    /// </summary>
    public class RimLLMTelemetryStore
    {
        private const string FileName = "RimLLM_Telemetry.json";
        private const int MaxChatHistoryEntries = 100;

        private readonly object _ioLock = new object();

        public List<string> ChatHistory { get; set; } = new List<string>();
        public List<RimLLMManager.RequestLogEntry> RequestLogs { get; set; } = new List<RimLLMManager.RequestLogEntry>();
        public long TotalPromptTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public float TotalEstimatedCost { get; set; }

        /// <summary>
        /// 磁碟上是否已存在遙測檔案。用於判斷是否需要從舊版設定 XML 遷移。
        /// </summary>
        public bool LoadedFromDisk { get; private set; }

        private class TelemetryDto
        {
            public List<string> ChatHistory;
            public List<RimLLMManager.RequestLogEntry> RequestLogs;
            public long TotalPromptTokens;
            public long TotalCompletionTokens;
            public float TotalEstimatedCost;
        }

        private static string GetFilePath()
        {
            return Path.Combine(GenFilePaths.ConfigFolderPath, FileName);
        }

        /// <summary>
        /// 從磁碟載入遙測資料。檔案不存在或格式錯誤時保留預設空值。
        /// </summary>
        public void Load()
        {
            lock (_ioLock)
            {
                try
                {
                    string path = GetFilePath();
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    var dto = JsonConvert.DeserializeObject<TelemetryDto>(File.ReadAllText(path));
                    if (dto == null)
                    {
                        return;
                    }

                    ChatHistory = dto.ChatHistory ?? new List<string>();
                    TrimChatHistory();
                    RequestLogs = dto.RequestLogs ?? new List<RimLLMManager.RequestLogEntry>();
                    TotalPromptTokens = dto.TotalPromptTokens;
                    TotalCompletionTokens = dto.TotalCompletionTokens;
                    TotalEstimatedCost = dto.TotalEstimatedCost;
                    LoadedFromDisk = true;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 載入遙測資料失敗，將使用空白統計: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 將遙測資料寫入磁碟。
        /// </summary>
        public void Save()
        {
            lock (_ioLock)
            {
                try
                {
                    TrimChatHistory();
                    var dto = new TelemetryDto
                    {
                        ChatHistory = ChatHistory,
                        RequestLogs = RequestLogs,
                        TotalPromptTokens = TotalPromptTokens,
                        TotalCompletionTokens = TotalCompletionTokens,
                        TotalEstimatedCost = TotalEstimatedCost
                    };
                    File.WriteAllText(GetFilePath(), JsonConvert.SerializeObject(dto, Formatting.None));
                    LoadedFromDisk = true;
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 寫入遙測資料失敗: {ex.Message}");
                }
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

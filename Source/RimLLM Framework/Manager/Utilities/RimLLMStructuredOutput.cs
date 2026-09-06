using System;
using RimLLM_Framework.Core;

namespace RimLLM_Framework.Manager
{
#pragma warning disable S101 // reason: RimLLM 為品牌縮寫，維持現狀
    /// <summary>
    /// 結構化輸出的反序列化與修復。
    /// </summary>
    internal static class RimLLMStructuredOutput
    {
        /// <summary>
        /// 結構化輸出的核心流程：直接解析 → JSON repair 回退 → LLM-assisted double-repair。
        /// </summary>
        internal static T Deserialize<T>(string rawResponse, IRimLLMSettings settings)
        {
            try
            {
                return RimLLMJsonHelper.DeserializeAndValidate<T>(rawResponse);
            }
            catch (Exception ex)
            {
                if (!settings.EnableJsonRepair)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse,
                        $"Unable to parse LLM response to target object {typeof(T).Name} (JSON Repair is disabled). Raw Response: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}",
                        ex);
                }

                string repairedJson = RimLLMJsonHelper.RepairJson(rawResponse);
                RimLLMLog.Warning($"[RimLLM] First JSON parse failed, attempting static repair. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}\nRepaired preview: {RimLLMLog.SanitizeForLog(repairedJson, 300)}\nError: {RimLLMLog.SanitizeForLog(ex.Message, 200)}");
                try
                {
                    string fallbackExtracted = RimLLMJsonHelper.ExtractJsonBlock(repairedJson);
                    return RimLLMJsonHelper.DeserializeAndValidate<T>(fallbackExtracted);
                }
                catch (Exception repairEx)
                {
                    throw new RimLLMException(
                        LLMError.InvalidResponse,
                        $"Unable to parse LLM response to target object {typeof(T).Name}. Response preview: {RimLLMLog.SanitizeForLog(rawResponse, 300)}. Parse error: {RimLLMLog.SanitizeForLog(ex.Message, 200)}. Static repair error: {RimLLMLog.SanitizeForLog(repairEx.Message, 200)}",
                        repairEx);
                }
            }
        }
    }
#pragma warning restore S101
}

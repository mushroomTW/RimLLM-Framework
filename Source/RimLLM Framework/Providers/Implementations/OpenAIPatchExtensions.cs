using System;
using System.Reflection;
using OpenAI.Chat;

namespace RimLLM_Framework.Providers
{
    /// <summary>
    /// OpenAI SDK 2.13.0 (搭配 System.ClientModel 1.15.0) 的 <see cref="ChatCompletionOptions.Patch"/>
    /// 內部註冊了未對 null 做防護的 PropagateSet/PropagateGet 委派。
    /// 當對根屬性（如 $.response_format、$.stream_options）操作 Patch 時，SDK 會直接對為 null 的子屬性調用
    /// 其 .Patch 屬性而擲出 <see cref="NullReferenceException"/>。
    ///
    /// 此擴充方法在對 Patch 進行自訂修改前，透過反射清空 _patch 的傳播委派，使其還原為直接寫入底層 JSON Patch 屬性字典的行為。
    /// </summary>
#pragma warning disable S3011 // reason: OpenAI SDK 2.13.0 的 _patch 欄位為 internal/private，需透過反射清空傳播委派以修復 NRE
    internal static class OpenAIPatchExtensions
    {
        private static readonly FieldInfo PatchField =
            typeof(ChatCompletionOptions).GetField("_patch", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo PropagatorSetterField =
            PatchField?.FieldType.GetField("_propagatorSetter", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo PropagatorGetterField =
            PatchField?.FieldType.GetField("_propagatorGetter", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// 停用 <see cref="ChatCompletionOptions"/> 內部 <see cref="System.ClientModel.Primitives.JsonPatch"/> 的屬性傳播器，
        /// 避免 OpenAI SDK 2.13.0 在子屬性為 null 時拋出 NullReferenceException。
        /// </summary>
        public static ChatCompletionOptions DisablePatchPropagators(this ChatCompletionOptions options)
        {
            if (options == null || PatchField == null || PropagatorSetterField == null) return options;

            try
            {
                object patchObj = PatchField.GetValue(options);
                if (patchObj != null)
                {
                    PropagatorSetterField.SetValue(patchObj, null);
                    PropagatorGetterField?.SetValue(patchObj, null);
                    PatchField.SetValue(options, patchObj);
                }
            }
            catch
            {
                // 防禦性捕獲，避免不同版本 runtime 例外
            }

            return options;
        }

        /// <summary>
        /// 從基礎工廠或新建實例中取得已停用傳播器的 <see cref="ChatCompletionOptions"/>。
        /// </summary>
        public static ChatCompletionOptions GetOrCreateSanitizedOptions(
            Func<Microsoft.Extensions.AI.IChatClient, object> baseFactory,
            Microsoft.Extensions.AI.IChatClient client)
        {
            return (baseFactory?.Invoke(client) as ChatCompletionOptions ?? new ChatCompletionOptions()).DisablePatchPropagators();
        }
    }
#pragma warning restore S3011
}

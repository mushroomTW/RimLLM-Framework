using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RimLLM_Framework.Compat;
using RimLLM_Framework.Mod;
using RimTalk.Client;
using RimTalk.Data;
using RimTalk.Error;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RimLLM_Framework.Tests
{
    /// <summary>
    /// RimTalk 相容層的純邏輯部分：角色對映、訊息轉換、日誌描述與設定登錄表。
    /// Harmony 攔截與實際串流需要遊戲執行期，交由實機驗證。
    /// </summary>
    [TestFixture]
    public class CompatRimTalkTests
    {
        // 1x1 像素的 JPEG 檔頭片段，只要是合法 base64 即可；轉換器不解碼圖片內容。
        private const string SampleImageBase64 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVN//2Q==";

        [Test]
        public void ToChatRole_MapsAllRimTalkRoles()
        {
            ClassicAssert.AreEqual(ChatRole.System, RimTalkMessageConverter.ToChatRole(Role.System));
            ClassicAssert.AreEqual(ChatRole.User, RimTalkMessageConverter.ToChatRole(Role.User));
            ClassicAssert.AreEqual(ChatRole.Assistant, RimTalkMessageConverter.ToChatRole(Role.AI));
        }

        [Test]
        public void Build_PreservesOrderAndMergesConsecutiveSameRole()
        {
            var prefix = new List<(Role role, string message)>
            {
                (Role.System, "Base instruction"),
                (Role.System, "Output JSONL."),
                (Role.User, "Context")
            };
            var messages = new List<(Role role, string message)>
            {
                (Role.User, "Dialogue prompt"),
                (Role.AI, "{\"name\":\"A\",\"text\":\"hi\"}")
            };

            List<ChatMessage> result = RimTalkMessageConverter.Build(prefix, messages, null);

            ClassicAssert.AreEqual(3, result.Count);
            ClassicAssert.AreEqual(ChatRole.System, result[0].Role);
            ClassicAssert.AreEqual("Base instruction\n\nOutput JSONL.", result[0].Text);
            ClassicAssert.AreEqual(ChatRole.User, result[1].Role);
            ClassicAssert.AreEqual("Context\n\nDialogue prompt", result[1].Text);
            ClassicAssert.AreEqual(ChatRole.Assistant, result[2].Role);
        }

        [Test]
        public void Build_ToleratesNullLists()
        {
            List<ChatMessage> result = RimTalkMessageConverter.Build(null, null, null);
            ClassicAssert.IsEmpty(result);
        }

        [Test]
        public void Build_AttachesImageToTrailingUserMessage()
        {
            var prefix = new List<(Role role, string message)> { (Role.System, "sys"), (Role.User, "look at this") };

            List<ChatMessage> result = RimTalkMessageConverter.Build(prefix, null, SampleImageBase64);

            ClassicAssert.AreEqual(2, result.Count);
            ChatMessage last = result[1];
            ClassicAssert.AreEqual(ChatRole.User, last.Role);
            ClassicAssert.AreEqual("look at this", last.Text);
            DataContent image = last.Contents.OfType<DataContent>().Single();
            ClassicAssert.AreEqual("image/jpeg", image.MediaType);
            CollectionAssert.AreEqual(Convert.FromBase64String(SampleImageBase64), image.Data.ToArray());
        }

        [Test]
        public void Build_AddsSeparateUserMessageWhenLastIsNotUser()
        {
            var messages = new List<(Role role, string message)> { (Role.User, "q"), (Role.AI, "a") };

            List<ChatMessage> result = RimTalkMessageConverter.Build(null, messages, SampleImageBase64);

            ClassicAssert.AreEqual(3, result.Count);
            ClassicAssert.AreEqual(ChatRole.Assistant, result[1].Role);
            ClassicAssert.AreEqual(ChatRole.User, result[2].Role);
            ClassicAssert.IsTrue(result[2].Contents.OfType<DataContent>().Any());
            ClassicAssert.IsFalse(result[1].Contents.OfType<DataContent>().Any());
        }

        [Test]
        public void DescribeRequest_OmitsBase64AndKeepsOpenAIShape()
        {
            var prefix = new List<(Role role, string message)> { (Role.System, "sys"), (Role.User, "hello") };
            List<ChatMessage> built = RimTalkMessageConverter.Build(prefix, null, SampleImageBase64);

            string json = RimTalkMessageConverter.DescribeRequest(built, stream: true, "test-model");

            StringAssert.Contains("\"model\":\"test-model\"", json.Replace(" ", string.Empty));
            StringAssert.Contains("\"stream\":true", json.Replace(" ", string.Empty));
            StringAssert.Contains("\"role\":\"system\"", json.Replace(" ", string.Empty));
            StringAssert.Contains("\"role\":\"user\"", json.Replace(" ", string.Empty));
            StringAssert.Contains("image/jpeg", json);
            StringAssert.DoesNotContain(SampleImageBase64, json);
        }

        [Test]
        public void CompatTakeover_DefaultsOffAndRoundTripsThroughSetter()
        {
            var settings = new RimLLMFrameworkSettings();

            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(null));

            settings.SetCompatTakeoverEnabled(RimTalkCompatTarget.PackageId, true);
            ClassicAssert.IsTrue(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled("never.registered"));

            settings.SetCompatTakeoverEnabled(RimTalkCompatTarget.PackageId, false);
            ClassicAssert.IsFalse(settings.IsCompatTakeoverEnabled(RimTalkCompatTarget.PackageId));

            // 空鍵不得寫入也不得擲例外
            settings.SetCompatTakeoverEnabled(null, true);
            settings.SetCompatTakeoverEnabled(string.Empty, true);
        }

        /// <summary>
        /// 模擬真實情境：模型以 JSONL 回三個 pawn 的對話，串流切在物件邊界的任意位置。
        /// 每湊齊一行就要回呼一次（氣泡逐個出現），而不是等整段結束。
        /// </summary>
        [Test]
        public async Task Streaming_FeedsRimTalkParserPerChunkAndReportsEachLineAsItCompletes()
        {
            var fake = new CapturingChatClient();
            fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "{\"name\":\"Alice\",\"te") { ModelId = "fake-model" });
            fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant,
                "xt\":\"Hi Bob\"}\n{\"name\":\"Bob\",\"text\":\"Hi {Alice}\",\"act\":\"Chat\",\"target\":\"Alice\"}\n{\"name\":\"Ca"));
            fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "rol\",\"text\":\"...\"}"));
            var usage = new ChatResponseUpdate { Contents = { new UsageContent(new UsageDetails { TotalTokenCount = 321 }) } };
            fake.StreamUpdates.Add(usage);

            var client = new RimTalkCompatClient(fake);
            var seen = new List<string>();
            var prefix = new List<(Role role, string message)> { (Role.System, "Output JSONL."), (Role.User, "talk") };
            Payload prepared = null;

            Payload payload = await client.GetStreamingChatCompletionAsync<TalkResponse>(prefix, new List<(Role, string)>(),
                onResponseParsed: r => seen.Add(r.Name),
                onRequestPrepared: p => prepared = p);

            ClassicAssert.IsNotNull(prepared);
            StringAssert.Contains("Output JSONL.", prepared.Request);
            ClassicAssert.AreEqual(3, seen.Count);
            CollectionAssert.AreEqual(new[] { "Alice", "Bob", "Carol" }, seen);
            ClassicAssert.AreEqual("fake-model", payload.Model);
            ClassicAssert.AreEqual(321, payload.TokenCount);
            ClassicAssert.AreEqual(200, payload.StatusCode);
            StringAssert.Contains("\"name\":\"Carol\"", payload.Response);
            ClassicAssert.AreEqual("rimllm://cj.rimtalk", payload.URL);

            // 轉接器把 RimTalk 訊息原樣送給 RimLLM client，且關閉思考。
            ChatMessage[] sent = fake.ReceivedMessages.Single().ToArray();
            ClassicAssert.AreEqual(ChatRole.System, sent[0].Role);
            ClassicAssert.IsTrue(fake.ReceivedOptions.Single() is RimLLMChatOptions opts && opts.DisableReasoning);
        }

        [Test]
        public async Task Streaming_IgnoresReasoningContentWhenSplittingJsonl()
        {
            var fake = new CapturingChatClient();
            var reasoning = new ChatResponseUpdate { Contents = { new TextReasoningContent("{\"name\":\"Ghost\",\"text\":\"should not surface\"}") } };
            fake.StreamUpdates.Add(reasoning);
            fake.StreamUpdates.Add(new ChatResponseUpdate(ChatRole.Assistant, "{\"name\":\"Real\",\"text\":\"ok\"}"));

            var client = new RimTalkCompatClient(fake);
            var names = new List<string>();
            Payload payload = await client.GetStreamingChatCompletionAsync<TalkResponse>(null, null, r => names.Add(r.Name));

            CollectionAssert.AreEqual(new[] { "Real" }, names);
            StringAssert.DoesNotContain("Ghost", payload.Response);
        }

        [Test]
        public async Task NonStreaming_ReturnsPayloadWithModelTextAndTokens()
        {
            var fake = new CapturingChatClient
            {
                ResponseFactory = () => new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"name\":\"A\",\"text\":\"b\"}"))
                {
                    ModelId = "fake-model",
                    Usage = new UsageDetails { TotalTokenCount = 42 }
                }
            };
            var client = new RimTalkCompatClient(fake);

            Payload payload = await client.GetChatCompletionAsync(
                new List<(Role role, string message)> { (Role.User, "q") }, new List<(Role, string)>());

            ClassicAssert.AreEqual("fake-model", payload.Model);
            ClassicAssert.AreEqual(42, payload.TokenCount);
            ClassicAssert.AreEqual("{\"name\":\"A\",\"text\":\"b\"}", payload.Response);
            ClassicAssert.IsNull(payload.ErrorMessage);
        }

        [Test]
        public void Errors_AreWrappedAsAIRequestExceptionWithPayload()
        {
            var fake = new CapturingChatClient
            {
                ResponseException = new RimLLMException(LLMError.RateLimit, "slow down")
            };
            var client = new RimTalkCompatClient(fake);

            var ex = Assert.ThrowsAsync<AIRequestException>(async () =>
                await client.GetChatCompletionAsync(null, new List<(Role role, string message)> { (Role.User, "q") }));

            StringAssert.Contains("RateLimit", ex.Message);
            StringAssert.Contains("slow down", ex.Message);
            ClassicAssert.IsNotNull(ex.Payload);
            ClassicAssert.AreEqual(ex.Message, ex.Payload.ErrorMessage);
            StringAssert.Contains("\"q\"", ex.Payload.Request);
        }

        [Test]
        public void RimTalkTarget_DeclaresStablePackageId()
        {
            var target = new RimTalkCompatTarget();
            ClassicAssert.AreEqual("cj.rimtalk", target.ModId);
            ClassicAssert.AreEqual("RimTalk", target.DisplayName);
            ClassicAssert.IsFalse(target.IsPatched);
        }
    }
}

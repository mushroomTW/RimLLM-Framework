extern alias bclasync;
using NUnit.Framework;
using System;
using System.Reflection;
using System.Collections.Generic;
using RimLLM_Framework.Core;
using RimLLM_Framework.Mod;

namespace RimLLM_Framework.Tests
{
    [TestFixture]
    public class CoreTests
    {
        [Test]
        public void TestEncryption()
        {
            string original = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
            string cipher = EncryptionUtility.Encrypt(original);
            Assert.IsNotEmpty(cipher);
            Assert.AreNotEqual(original, cipher);

            string decrypted = EncryptionUtility.Decrypt(cipher);
            Assert.AreEqual(original, decrypted);

            Assert.AreEqual("", EncryptionUtility.Encrypt(""));
            Assert.AreEqual("", EncryptionUtility.Decrypt(""));
        }

                [Test]
        public void TestDynamicHardwareSalt()
        {
            string original = "sensitive-api-key";
            
            // 1. 設定 Salt-A
            EncryptionUtility.CustomSalt = "SaltA";
            EncryptionUtility.InitializeKeyAndIv();
            string cipherA = EncryptionUtility.Encrypt(original);
            
            // 2. 設定 Salt-B
            EncryptionUtility.CustomSalt = "SaltB";
            EncryptionUtility.InitializeKeyAndIv();
            string cipherB = EncryptionUtility.Encrypt(original);

            Assert.AreNotEqual(cipherA, cipherB); // 不同 Salt 加密出的結果應該不同

            // 3. 驗證同 Salt 可以解密，異 Salt 會解密失敗或解出空字串
            EncryptionUtility.CustomSalt = "SaltA";
            EncryptionUtility.InitializeKeyAndIv();
            string decryptedA = EncryptionUtility.Decrypt(cipherA);
            Assert.AreEqual(original, decryptedA);

            string decryptedB = EncryptionUtility.Decrypt(cipherB);
            Assert.AreNotEqual(original, decryptedB); // 異 Salt 解密失敗
        }

        [Test]
        public void TestSanitizeForLogRedactsProviderSpecificKeys()
        {
            var secrets = new Dictionary<string, string>
            {
                { "Google", "AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7" },
                { "Groq", "gsk_abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Grok", "xai-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Nvidia", "nvapi-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "OpenAI", "sk-abcdefghijklmnopqrstuvwxyz0123456789" },
                { "Anthropic", "sk-ant-abcdefghijklmnopqrstuvwxyz0123" }
            };

            foreach (var kvp in secrets)
            {
                string sanitized = RimLLMLog.SanitizeForLog($"request failed with key {kvp.Value}");
                Assert.IsFalse(sanitized.Contains(kvp.Value),
                    $"日誌遮罩必須涵蓋全部供應商的金鑰格式（{kvp.Key} 未被遮罩）");
            }

            string bearer = RimLLMLog.SanitizeForLog("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345");
            Assert.IsFalse(bearer.Contains("abcdefghijklmnopqrstuvwxyz012345"), "Bearer token 必須被遮罩");
        }

        [Test]
        public void TestSanitizeForLogTruncatesAndEscapesNewlines()
        {
            string sanitized = RimLLMLog.SanitizeForLog("line1\r\nline2", 500);
            Assert.IsFalse(sanitized.Contains("\n"), "換行必須被跳脫以防日誌注入");
            Assert.IsTrue(sanitized.Contains("\\r\\n"));

            string longText = new string('x', 600);
            Assert.IsTrue(RimLLMLog.SanitizeForLog(longText, 100).Length <= 103, "超長內容必須被截斷");
        }

        [Test]
        public void TestDecryptFailureReturnsNullInsteadOfEmpty()
        {
            // 無法解密的內容（既非 v2 格式也不是合法 Base64 密文）
            string result = EncryptionUtility.Decrypt("v2:bm90LWEtdmFsaWQtcGF5bG9hZA==");

            Assert.IsNull(result,
                "解密失敗必須回傳 null 而非空字串，呼叫端才能保留原始密文而不覆寫為空");
            Assert.AreEqual(string.Empty, EncryptionUtility.Decrypt(""),
                "空輸入仍應回傳空字串，與解密失敗區分");
        }

        [Test]
        public void TestLanguageKeysAreConsistentAcrossLocales()
        {
            string repoRoot = FindRepositoryRoot();
            if (repoRoot == null)
            {
                Assert.Ignore("找不到 repository 根目錄，略過語言檔一致性檢查。");
            }

            var localeKeys = new Dictionary<string, List<string>>();
            foreach (string locale in new[] { "English", "ChineseSimplified", "ChineseTraditional" })
            {
                string path = System.IO.Path.Combine(repoRoot, "Languages", locale, "Keyed", "Keys.xml");
                Assert.IsTrue(System.IO.File.Exists(path), $"缺少語言檔: {path}");

                var doc = System.Xml.Linq.XDocument.Load(path);
                var keys = new List<string>();
                foreach (var element in doc.Root.Elements())
                {
                    keys.Add(element.Name.LocalName);
                }

                var duplicates = new List<string>();
                var seen = new HashSet<string>();
                foreach (string key in keys)
                {
                    if (!seen.Add(key)) duplicates.Add(key);
                }
                Assert.IsEmpty(duplicates, $"{locale} 語言檔含重複鍵: {string.Join(", ", duplicates.ToArray())}");

                keys.Sort(StringComparer.Ordinal);
                localeKeys[locale] = keys;
            }

            CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseTraditional"],
                "三語系語言檔的鍵集合必須完全一致");
            CollectionAssert.AreEqual(localeKeys["English"], localeKeys["ChineseSimplified"],
                "三語系語言檔的鍵集合必須完全一致");
        }

        private static string FindRepositoryRoot()
        {
            string envRoot = System.Environment.GetEnvironmentVariable("RIMLLM_REPO_ROOT");
            if (!string.IsNullOrEmpty(envRoot) && System.IO.Directory.Exists(envRoot))
            {
                return envRoot;
            }

            var dir = new System.IO.DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "About", "About.xml")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }
    }
}

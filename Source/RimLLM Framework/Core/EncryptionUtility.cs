using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 提供統一的 AES-256 對稱加密與解密工具，用於加密設定檔中的敏感金鑰（API Keys）。
    /// </summary>
    public static class EncryptionUtility
    {
        // 混淆金鑰與初始化向量
        private static byte[] Key;
        private static byte[] Iv;
        private static byte[] MacKey;
        private static readonly object CryptLock = new object();
        private const string VersionPrefix = "v2:";

        // 允許單元測試注入自訂的 Salt
        private static string _customSalt = null;
        public static string CustomSalt
        {
            get
            {
                lock (CryptLock)
                {
                    return _customSalt;
                }
            }
            set
            {
                lock (CryptLock)
                {
                    _customSalt = value;
                }
            }
        }

        static EncryptionUtility()
        {
            InitializeKeyAndIv();
        }

        public static void InitializeKeyAndIv()
        {
            lock (CryptLock)
            {
                // 使用固定的混淆字串與 SHA256/MD5 來產生金鑰與 IV，避免程式碼中直接存在明文 Byte 陣列
                string rawKeySeed = "RimLLMSecretKeySeed2026";
                string rawIvSeed = "RimLLMSecretIvSeed2026";

                string hardwareSalt = "";
                try
                {
                    if (!string.IsNullOrEmpty(_customSalt))
                    {
                        hardwareSalt = _customSalt;
                    }
                    else
                    {
                        hardwareSalt = UnityEngine.SystemInfo.deviceUniqueIdentifier;
                    }
                }
                catch
                {
                    hardwareSalt = "UnityMockEnvironmentSalt";
                }

                if (string.IsNullOrEmpty(hardwareSalt) || hardwareSalt == "n/a")
                {
                    hardwareSalt = "DefaultHardwareSaltFallback";
                }

                rawKeySeed += hardwareSalt;
                rawIvSeed += hardwareSalt;
 
                using (SHA256 sha256 = SHA256.Create())
                {
                    Key = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed));
                }
 
                using (MD5 md5 = MD5.Create())
                {
                    Iv = md5.ComputeHash(Encoding.UTF8.GetBytes(rawIvSeed));
                }

                using (SHA256 sha256 = SHA256.Create())
                {
                    MacKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed + ":mac"));
                }
            }
        }
 
        /// <summary>
        /// 加密字串，回傳 Base64 加密密文。
        /// </summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;
 
            lock (CryptLock)
            {
                try
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = Key;
                        aes.GenerateIV();

                        using (ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                            {
                                using (StreamWriter sw = new StreamWriter(cs))
                                {
                                    sw.Write(plainText);
                                }
                            }
                            byte[] cipherBytes = ms.ToArray();
                            byte[] payload = Combine(aes.IV, cipherBytes);
                            byte[] mac = ComputeMac(payload);
                            return VersionPrefix + Convert.ToBase64String(Combine(payload, mac));
                        }
                    }
                }
                catch (Exception ex)
                {
                    RimLLMLog.Error($"[RimLLM] 加密金鑰時發生異常: {ex.Message}");
                    throw new RimLLM_Framework.SDK.RimLLMException(RimLLM_Framework.SDK.LLMError.Unknown, $"Encryption failed: {ex.Message}", ex);
                }
            }
        }
 
        /// <summary>
        /// 解密 Base64 密文，回傳原始字串。
        /// </summary>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;
 
            lock (CryptLock)
            {
                try
                {
                    if (cipherText.StartsWith(VersionPrefix, StringComparison.Ordinal))
                    {
                        return DecryptV2(cipherText.Substring(VersionPrefix.Length));
                    }

                    byte[] buffer = Convert.FromBase64String(cipherText);
 
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = Key;
                        aes.IV = Iv;
 
                        using (ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                        using (MemoryStream ms = new MemoryStream(buffer))
                        {
                            using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                            {
                                using (StreamReader sr = new StreamReader(cs))
                                {
                                    return sr.ReadToEnd();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RimLLMLog.Warning($"[RimLLM] 解密金鑰失敗 (可能格式錯誤或金鑰受損): {ex.Message}");
                    return string.Empty;
                }
            }
        }

        private static string DecryptV2(string encodedPayload)
        {
            byte[] allBytes = Convert.FromBase64String(encodedPayload);
            const int ivLength = 16;
            const int macLength = 32;

            if (allBytes.Length <= ivLength + macLength)
            {
                throw new CryptographicException("Encrypted payload is too short.");
            }

            int cipherLength = allBytes.Length - ivLength - macLength;
            byte[] iv = new byte[ivLength];
            byte[] cipherBytes = new byte[cipherLength];
            byte[] expectedMac = new byte[macLength];

            Buffer.BlockCopy(allBytes, 0, iv, 0, ivLength);
            Buffer.BlockCopy(allBytes, ivLength, cipherBytes, 0, cipherLength);
            Buffer.BlockCopy(allBytes, ivLength + cipherLength, expectedMac, 0, macLength);

            byte[] payload = Combine(iv, cipherBytes);
            byte[] actualMac = ComputeMac(payload);
            if (!FixedTimeEquals(expectedMac, actualMac))
            {
                throw new CryptographicException("Encrypted payload authentication failed.");
            }

            using (Aes aes = Aes.Create())
            {
                aes.Key = Key;
                aes.IV = iv;

                using (ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                using (MemoryStream ms = new MemoryStream(cipherBytes))
                using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private static byte[] ComputeMac(byte[] payload)
        {
            using (var hmac = new HMACSHA256(MacKey))
            {
                return hmac.ComputeHash(payload);
            }
        }

        private static byte[] Combine(byte[] first, byte[] second)
        {
            byte[] combined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
            Buffer.BlockCopy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }
    }
}

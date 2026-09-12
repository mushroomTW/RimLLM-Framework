using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace RimLLM_Framework.Core
{
    /// <summary>
    /// 提供統一的 AES-256 對稱加密與解密工具，用於保護設定檔與遙測中的敏感資料。
    /// 新資料的 AES 金鑰以每使用者的 OS 保護秘密保存；舊版裝置衍生金鑰僅供遷移既有密文。
    /// </summary>
    public static class EncryptionUtility
    {
        // 舊版由裝置識別值衍生的金鑰，僅供既有密文遷移解密。
        private static byte[] Key;
        private static byte[] Iv;
        private static byte[] MacKey;
        private static byte[] _secureKey;
        private static readonly object CryptLock = new object();
        private const string SecureVersionPrefix = "v3:";
        private const string LegacyVersionPrefix = "v2:";
        private const string LegacyKeySeed = "RimLLMSecretKeySeed2026";
        private const string LegacyIvSeed = "RimLLMSecretIvSeed2026";
        private const string SecureKeyFileName = "RimLLM_EncryptionKey.dat";
        private const int AesKeyLength = 32;
        private static readonly byte[] SecureKeyEntropy =
            Encoding.UTF8.GetBytes("GreenMushroom.RimLLMFramework");

        // 讓測試使用隔離的 key 檔案；正式環境固定走每使用者的 OS 保護儲存。
        internal static Func<string> SecureKeyPathResolver = GetDefaultSecureKeyPath;

        // 允許單元測試注入舊版 Salt；不參與新的安全格式。
        private static string _customSalt;
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

        internal static void ResetSecureKeyForTests()
        {
            lock (CryptLock)
            {
                _secureKey = null;
                SecureKeyPathResolver = GetDefaultSecureKeyPath;
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
                // 僅為解開既有 v1/v2 密文保留；新資料不再使用這些值產生金鑰。
                string rawKeySeed = LegacyKeySeed;
                string rawIvSeed = LegacyIvSeed;

                string hardwareSalt;
                try
                {
                    hardwareSalt = !string.IsNullOrEmpty(_customSalt)
                        ? _customSalt
                        : UnityEngine.SystemInfo.deviceUniqueIdentifier;
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
 
#pragma warning disable S4790 // reason: 設備指紋衍生的 IV 派生，非密碼儲存，已有 SHA256 主金鑰，MD5 僅用於產生 16-byte IV 長度需求
                using (MD5 md5 = MD5.Create())
                {
                    Iv = md5.ComputeHash(Encoding.UTF8.GetBytes(rawIvSeed));
                }
#pragma warning restore S4790

                using (SHA256 sha256 = SHA256.Create())
                {
                    MacKey = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed + ":mac"));
                }
            }
        }

        private static void GetSecureKeySnapshot(out byte[] key, out byte[] macKey)
        {
            lock (CryptLock)
            {
                if (_secureKey == null)
                {
                    _secureKey = LoadOrCreateSecureKey();
                }

                key = (byte[])_secureKey.Clone();
            }

            macKey = DeriveSecureMacKey(key);
        }

        private static byte[] LoadOrCreateSecureKey()
        {
            string path = SecureKeyPathResolver();
            if (string.IsNullOrEmpty(path))
            {
                throw new CryptographicException("Secure key path is unavailable.");
            }

            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new CryptographicException("Secure key directory is unavailable.");
            }

            Directory.CreateDirectory(directory);
            if (File.Exists(path))
            {
                return UnprotectStoredKey(File.ReadAllBytes(path));
            }

            byte[] key = new byte[AesKeyLength];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(key);
            }

            byte[] protectedKey = ProtectedData.Protect(
                key,
                SecureKeyEntropy,
                DataProtectionScope.CurrentUser);

            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Write(protectedKey, 0, protectedKey.Length);
                }

                return key;
            }
            catch (IOException) when (File.Exists(path))
            {
                // 另一個程序可能同時建立了同一個使用者的 key；採用先成功寫入的版本。
                return UnprotectStoredKey(File.ReadAllBytes(path));
            }
        }

        private static byte[] UnprotectStoredKey(byte[] protectedKey)
        {
            if (protectedKey == null || protectedKey.Length == 0)
            {
                throw new CryptographicException("Stored secure key is empty.");
            }

            byte[] key = ProtectedData.Unprotect(
                protectedKey,
                SecureKeyEntropy,
                DataProtectionScope.CurrentUser);
            if (key == null || key.Length != AesKeyLength)
            {
                throw new CryptographicException("Stored secure key has an invalid length.");
            }

            return key;
        }

        private static string GetDefaultSecureKeyPath()
        {
            try
            {
                string configFolder = GenFilePaths.ConfigFolderPath;
                if (!string.IsNullOrEmpty(configFolder))
                {
                    return Path.Combine(configFolder, SecureKeyFileName);
                }
            }
            catch
            {
                // 測試或 headless 環境可能尚未初始化 Verse，改用同一使用者的應用程式資料夾。
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new CryptographicException("Per-user application data path is unavailable.");
            }

            return Path.Combine(localAppData, "RimLLM Framework", SecureKeyFileName);
        }

        private static byte[] DeriveSecureMacKey(byte[] key)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes("RimLLM Framework secure storage v3"));
            }
        }
 
        private static void GetKeySnapshot(out byte[] key, out byte[] iv, out byte[] macKey)
        {
            lock (CryptLock)
            {
                if (Key == null || Iv == null || MacKey == null)
                {
                    InitializeKeyAndIv();
                }
                key = Key;
                iv = Iv;
                macKey = MacKey;
            }
        }

        /// <summary>
        /// 加密字串，回傳 Base64 加密密文。
        /// </summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                GetSecureKeySnapshot(out byte[] key, out byte[] macKey);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
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
                        byte[] mac = ComputeMac(payload, macKey);
                        return SecureVersionPrefix + Convert.ToBase64String(Combine(payload, mac));
                    }
                }
            }
            catch (Exception ex)
            {
                RimLLMLog.Error($"[RimLLM] 加密金鑰時發生異常: {ex.Message}");
                throw new RimLLMException(LLMError.Unknown, $"Encryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 解密 Base64 密文，回傳原始字串。
        /// </summary>
        /// <remarks>
        /// 解密失敗時回傳 <c>null</c>（而非空字串），讓呼叫端能區分「金鑰本來就是空的」
        /// 與「金鑰存在但解不開」。新格式使用每使用者的 OS 保護金鑰，
        /// 舊格式仍以 deviceUniqueIdentifier 作為遷移 salt；換硬體、換使用者或換平台可能使既有密文解不開；若此時回傳空字串，
        /// 呼叫端下次存檔就會以 Encrypt("") 覆寫，造成金鑰靜默永久遺失。
        /// </remarks>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;

            try
            {
                if (cipherText.StartsWith(SecureVersionPrefix, StringComparison.Ordinal))
                {
                    GetSecureKeySnapshot(out byte[] key, out byte[] macKey);
                    return DecryptV2(cipherText.Substring(SecureVersionPrefix.Length), key, macKey);
                }

                GetKeySnapshot(out byte[] legacyKey, out byte[] legacyIv, out byte[] legacyMacKey);
                if (cipherText.StartsWith(LegacyVersionPrefix, StringComparison.Ordinal))
                {
                    return DecryptV2(cipherText.Substring(LegacyVersionPrefix.Length), legacyKey, legacyMacKey);
                }

                byte[] buffer = Convert.FromBase64String(cipherText);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = legacyKey;
                    aes.IV = legacyIv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

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
                return null;
            }
        }

        private static string DecryptV2(string encodedPayload, byte[] key, byte[] macKey)
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
            byte[] actualMac = ComputeMac(payload, macKey);
            if (!FixedTimeEquals(expectedMac, actualMac))
            {
                throw new CryptographicException("Encrypted payload authentication failed.");
            }

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                using (MemoryStream ms = new MemoryStream(cipherBytes))
                using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        private static byte[] ComputeMac(byte[] payload, byte[] macKey)
        {
            using (var hmac = new HMACSHA256(macKey))
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

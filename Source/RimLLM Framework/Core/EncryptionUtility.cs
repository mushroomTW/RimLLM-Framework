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
        private static readonly byte[] Key;
        private static readonly byte[] Iv;

        static EncryptionUtility()
        {
            // 使用固定的混淆字串與 SHA256/MD5 來產生金鑰與 IV，避免程式碼中直接存在明文 Byte 陣列
            string rawKeySeed = "ArchotechNexusRimLLMSecretKeySeed2026";
            string rawIvSeed = "ArchotechNexusRimLLMSecretIvSeed2026";

            using (SHA256 sha256 = SHA256.Create())
            {
                Key = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawKeySeed));
            }

            using (MD5 md5 = MD5.Create())
            {
                Iv = md5.ComputeHash(Encoding.UTF8.GetBytes(rawIvSeed));
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
                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = Iv;

                    ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            using (StreamWriter sw = new StreamWriter(cs))
                            {
                                sw.Write(plainText);
                            }
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                ArchotechLog.Error($"[ArchotechNexus] 加密金鑰時發生異常: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// 解密 Base64 密文，回傳原始字串。
        /// </summary>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;

            try
            {
                byte[] buffer = Convert.FromBase64String(cipherText);

                using (Aes aes = Aes.Create())
                {
                    aes.Key = Key;
                    aes.IV = Iv;

                    ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

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
                ArchotechLog.Warning($"[ArchotechNexus] 解密金鑰失敗 (可能格式錯誤或金鑰受損): {ex.Message}");
                return string.Empty;
            }
        }
    }
}

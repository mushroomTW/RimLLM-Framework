using System;
using System.Reflection;
using RimLLM_Framework.Core;
using RimLLM_Framework.SDK;

namespace RimLLM_Framework.Tests
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("========================================");
            Console.WriteLine("Archotech Nexus - 核心組件單元測試開始");
            Console.WriteLine("========================================");

            bool allPassed = true;

            allPassed &= TestEncryption();
            allPassed &= TestClientRegistry();

            Console.WriteLine();
            Console.WriteLine("========================================");
            if (allPassed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("測試結果: 全部通過 (ALL PASSED)!");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("測試結果: 有部分測試失敗 (FAILED)!");
            }
            Console.ResetColor();
            Console.WriteLine("========================================");

            Environment.Exit(allPassed ? 0 : 1);
        }

        static bool TestEncryption()
        {
            Console.WriteLine("\n[1] 測試 AES 對稱加解密組件...");
            try
            {
                string original = "sk-proj-1234567890abcdefghijklmnopqrstuvwxyz";
                Console.WriteLine($"原始金鑰: {original}");

                // 1. 測試加密
                string cipher = EncryptionUtility.Encrypt(original);
                Console.WriteLine($"加密密文: {cipher}");
                if (string.IsNullOrEmpty(cipher) || cipher == original)
                {
                    Console.WriteLine("-> 加密失敗：密文為空或等於原文");
                    return false;
                }

                // 2. 測試解密
                string decrypted = EncryptionUtility.Decrypt(cipher);
                Console.WriteLine($"解密還原: {decrypted}");
                if (decrypted != original)
                {
                    Console.WriteLine("-> 解密失敗：還原內容與原文不符");
                    return false;
                }

                // 3. 測試邊界條件
                if (EncryptionUtility.Encrypt("") != "" || EncryptionUtility.Decrypt("") != "")
                {
                    Console.WriteLine("-> 邊界失敗：空字串加解密未正確處理");
                    return false;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-> AES 加解密測試通過!");
                Console.ResetColor();
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"-> 測試過程拋出異常: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        static bool TestClientRegistry()
        {
            Console.WriteLine("\n[2] 測試 ClientRegistry 來源驗證組件...");
            try
            {
                Assembly thisAssembly = Assembly.GetExecutingAssembly();
                Assembly externalAssembly = typeof(string).Assembly; // 使用 mscorlib 作為外部組件模擬

                string modId = "test.mod.id";

                // 1. 測試正常註冊與驗證
                ClientRegistry.RegisterClient(modId, thisAssembly);
                if (!ClientRegistry.Verify(modId, thisAssembly))
                {
                    Console.WriteLine("-> 正常校驗失敗：已註冊的組件 Verify 回傳 false");
                    return false;
                }

                // 2. 測試防冒用校驗 (不同的 Assembly 試圖冒用 modId)
                if (ClientRegistry.Verify(modId, externalAssembly))
                {
                    Console.WriteLine("-> 安全校驗漏失：未授權的組件 Verify 回傳 true");
                    return false;
                }

                // 3. 測試重複註冊衝突
                try
                {
                    ClientRegistry.RegisterClient(modId, externalAssembly);
                    Console.WriteLine("-> 重複註冊失敗：重複註冊相同 Mod ID 到不同的 Assembly 未拋出異常");
                    return false;
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine("-> 成功攔截不同組件重複註冊相同 Mod ID");
                }

                // 4. 測試未註冊自動補註冊容錯
                string newModId = "unregistered.mod.id";
                if (!ClientRegistry.Verify(newModId, thisAssembly))
                {
                    Console.WriteLine("-> 容錯校驗失敗：未註冊的 Mod ID 進行 Verify 未能自動補註冊並通過");
                    return false;
                }
                
                if (!ClientRegistry.Verify(newModId, thisAssembly))
                {
                    Console.WriteLine("-> 補註冊後校驗失敗");
                    return false;
                }
                
                if (ClientRegistry.Verify(newModId, externalAssembly))
                {
                    Console.WriteLine("-> 自動補註冊後，安全校驗漏失");
                    return false;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-> ClientRegistry 來源校驗測試通過!");
                Console.ResetColor();
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"-> 測試過程拋出異常: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }
    }
}

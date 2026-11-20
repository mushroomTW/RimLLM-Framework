#nullable disable

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;

namespace RimLLM_Framework.Core;

/// <summary>透過 libsecret 使用登入工作階段的 Secret Service，不將秘密放進程序參數或檔案。</summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxSecretStore : ISecretStore
{
    private const string Secret = "libsecret-1.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    public byte[] Read(string account)
    {
        using var call = new SecretCall(account);
        IntPtr value = secret_password_lookupv_sync(IntPtr.Zero, call.Attributes, call.Cancellable, out IntPtr error);
        try
        {
            CheckError(error);
            // 項目不存在代表尚未建立金鑰；回傳空集合，讓上層以「金鑰無效」統一回報。
            return value == IntPtr.Zero ? [] : Convert.FromBase64String(Marshal.PtrToStringUTF8(value));
        }
        finally { if (value != IntPtr.Zero) secret_password_free(value); }
    }

    public void Write(string account, byte[] key)
    {
        using var call = new SecretCall(account);
        int success = secret_password_storev_sync(IntPtr.Zero, call.Attributes, null, "RimLLM Framework storage key",
            Convert.ToBase64String(key), call.Cancellable, out IntPtr error);
        CheckError(error);
        if (success == 0) throw new CryptographicException("Secret Service did not store the storage key.");
    }

    public void Delete(string account)
    {
        using var call = new SecretCall(account);
        // 項目不存在時 libsecret 同樣回傳 FALSE；刪除本身是冪等操作，因此結果不影響流程。
        _ = secret_password_clearv_sync(IntPtr.Zero, call.Attributes, call.Cancellable, out IntPtr error);
        CheckError(error);
    }

    private static void CheckError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            // 原生訊息不帶進日誌，避免服務提供者將敏感內容寫入錯誤文字。
            int code = Marshal.ReadInt32(error, sizeof(uint));
            throw new CryptographicException($"Secret Service failed (code {code}). Ensure the session D-Bus and an unlocked keyring are available.");
        }
        finally { g_error_free(error); }
    }

    private sealed partial class SecretCall : IDisposable
    {
        private IntPtr _glib;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
        private CancellationTokenRegistration _registration;
        public IntPtr Attributes { get; private set; }
        public IntPtr Cancellable { get; private set; }

        public SecretCall(string account)
        {
            try
            {
                _glib = NativeLibrary.Load(Glib);
                Attributes = g_hash_table_new_full(NativeLibrary.GetExport(_glib, "g_str_hash"), NativeLibrary.GetExport(_glib, "g_str_equal"),
                    NativeLibrary.GetExport(_glib, "g_free"), NativeLibrary.GetExport(_glib, "g_free"));
                if (Attributes == IntPtr.Zero) throw new CryptographicException("Could not allocate Secret Service attributes.");
                InsertAttribute("application", "GreenMushroom.RimLLMFramework");
                InsertAttribute("account", account);
                Cancellable = g_cancellable_new();
                if (Cancellable == IntPtr.Zero) throw new CryptographicException("Could not allocate Secret Service cancellation.");
                _registration = _timeout.Token.Register(() => g_cancellable_cancel(Cancellable));
            }
            catch (Exception ex)
            {
                Dispose();
                throw new CryptographicException("Linux secure storage requires libsecret-1 and a session Secret Service.", ex);
            }
        }

        /// <summary>寫入一組查詢屬性；插入失敗代表重複鍵，屬程式錯誤，直接中止。</summary>
        private void InsertAttribute(string name, string value)
        {
            if (g_hash_table_insert(Attributes, g_strdup(name), g_strdup(value)) == 0)
                throw new CryptographicException($"Could not set the Secret Service attribute '{name}'.");
        }

        public void Dispose()
        {
            // 先等待取消回呼結束，才釋放它可能還在使用的原生物件。
            _registration.Dispose();
            _timeout.Dispose();
            if (Cancellable != IntPtr.Zero) { g_object_unref(Cancellable); Cancellable = IntPtr.Zero; }
            if (Attributes != IntPtr.Zero) { g_hash_table_unref(Attributes); Attributes = IntPtr.Zero; }
            if (_glib != IntPtr.Zero) { NativeLibrary.Free(_glib); _glib = IntPtr.Zero; }
        }

        [LibraryImport(Glib)]
        private static partial IntPtr g_hash_table_new_full(IntPtr hash, IntPtr equal, IntPtr freeKey, IntPtr freeValue);
        [LibraryImport(Glib)]
        private static partial int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
        [LibraryImport(Glib)]
        private static partial void g_hash_table_unref(IntPtr table);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr g_strdup(string text);
        [LibraryImport(Gio)]
        private static partial IntPtr g_cancellable_new();
        [LibraryImport(Gio)]
        private static partial void g_cancellable_cancel(IntPtr cancellable);
        [LibraryImport(GObject)]
        private static partial void g_object_unref(IntPtr instance);
    }

    [LibraryImport(Secret)]
    private static partial IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [LibraryImport(Secret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        string collection, string label, string password, IntPtr cancellable, out IntPtr error);
    [LibraryImport(Secret)]
    private static partial int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [LibraryImport(Secret)]
    private static partial void secret_password_free(IntPtr password);
    [LibraryImport(Glib)]
    private static partial void g_error_free(IntPtr error);
}

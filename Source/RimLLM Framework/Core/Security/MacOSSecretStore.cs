using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RimLLM_Framework.Core;

/// <summary>以 SecItem API 存取 macOS Keychain，不呼叫會暴露秘密參數的 security CLI。</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOSSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int NotFound = -25300;
    // 常數與 callback 指標依賴 framework 的生命週期；延遲載入並保留到程序結束。
    private static readonly Lazy<IntPtr> SecurityModule = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<IntPtr> CoreModule = new(() => NativeLibrary.Load(CoreFoundation));

    public byte[] Read(string account)
    {
        using var query = CreateQuery(account);
        CFDictionarySetValue(query.Handle, Constant(Security, "kSecReturnData"), Constant(CoreFoundation, "kCFBooleanTrue"));
        CFDictionarySetValue(query.Handle, Constant(Security, "kSecMatchLimit"), Constant(Security, "kSecMatchLimitOne"));
        int status = SecItemCopyMatching(query.Handle, out IntPtr result);
        using var data = new CFHandle(result);
        // 項目不存在代表尚未建立金鑰；回傳空集合，讓上層以「金鑰無效」統一回報。
        if (status == NotFound) return Array.Empty<byte>();
        CheckStatus(status);
        if (result == IntPtr.Zero || CFDataGetLength(result) != 32)
            throw new CryptographicException("The Keychain storage key has an invalid length.");
        byte[] key = new byte[32];
        Marshal.Copy(CFDataGetBytePtr(result), key, 0, key.Length);
        return key;
    }

    public void Write(string account, byte[] key)
    {
        using var query = CreateQuery(account);
        using var data = new CFHandle(CFDataCreate(IntPtr.Zero, key, key.Length));
        if (data.Handle == IntPtr.Zero) throw new CryptographicException("Could not allocate Keychain data.");
        CFDictionarySetValue(query.Handle, Constant(Security, "kSecValueData"), data.Handle);
        // 新項目使用全新 GUID；若遇到重複項目則拒絕覆寫，避免替換仍被密文引用的秘密。
        CheckStatus(SecItemAdd(query.Handle, IntPtr.Zero));
    }

    public void Delete(string account)
    {
        using var query = CreateQuery(account);
        int status = SecItemDelete(query.Handle);
        if (status != NotFound) CheckStatus(status);
    }

    private static CFHandle CreateQuery(string account)
    {
        var query = new CFHandle(CFDictionaryCreateMutable(IntPtr.Zero, 0,
            NativeLibrary.GetExport(CoreModule.Value, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CoreModule.Value, "kCFTypeDictionaryValueCallBacks")));
        try
        {
            if (query.Handle == IntPtr.Zero) throw new CryptographicException("Could not allocate a Keychain query.");
            CFDictionarySetValue(query.Handle, Constant(Security, "kSecClass"), Constant(Security, "kSecClassGenericPassword"));
            using var service = CreateString("GreenMushroom.RimLLMFramework");
            using var item = CreateString(account);
            CFDictionarySetValue(query.Handle, Constant(Security, "kSecAttrService"), service.Handle);
            CFDictionarySetValue(query.Handle, Constant(Security, "kSecAttrAccount"), item.Handle);
            return query;
        }
        catch { query.Dispose(); throw; }
    }

    private static CFHandle CreateString(string value)
    {
        var result = new CFHandle(CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100));
        if (result.Handle == IntPtr.Zero) throw new CryptographicException("Could not allocate a Keychain attribute.");
        return result;
    }

    private static IntPtr Constant(string library, string symbol)
    {
        IntPtr module = library == Security ? SecurityModule.Value : CoreModule.Value;
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(module, symbol));
    }

    private static void CheckStatus(int status)
    {
        if (status != 0)
            throw new CryptographicException($"Keychain access failed (OSStatus {status}). Ensure the login keychain is unlocked and RimWorld is allowed access.");
    }

    private sealed partial class CFHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;
        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { CFRelease(Handle); Handle = IntPtr.Zero; }
        }

        [LibraryImport(CoreFoundation)]
        private static partial void CFRelease(IntPtr value);
    }

    [LibraryImport(Security)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [LibraryImport(Security)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);
    [LibraryImport(Security)]
    private static partial int SecItemDelete(IntPtr query);
    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [LibraryImport(CoreFoundation)]
    private static partial void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDataCreate(IntPtr allocator,
        [In][MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] bytes, nint length);
    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(IntPtr data);
    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);
}

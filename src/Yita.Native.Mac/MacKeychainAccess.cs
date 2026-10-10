using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Yita.Native.Mac;

internal interface IMacKeychainAccess
{
    byte[]? Read(string service, string account);
    bool TryAdd(string service, string account, byte[] data);
    void Write(string service, string account, byte[] data);
    void Delete(string service, string account);
}

internal sealed class MacKeychainException(int status)
    : CryptographicException($"macOS Keychain is unavailable (status {status}). Unlock it or allow Yita access.")
{
    internal int Status { get; } = status;
}

internal sealed class MacKeychainAccess : IMacKeychainAccess
{
    private const int NotFound = -25300;
    private const int Duplicate = -25299;
    private const int MaximumSecretBytes = 64 * 1024;

    public byte[]? Read(string service, string account)
    {
        EnsurePlatform();
        using var query = Query(service, account);
        query.Set(Native.SecurityConstant("kSecReturnData"), Native.CoreConstant("kCFBooleanTrue"));
        query.Set(Native.SecurityConstant("kSecMatchLimit"), Native.SecurityConstant("kSecMatchLimitOne"));
        var status = Native.SecItemCopyMatching(query.Handle, out var result);
        try
        {
            if (status == NotFound) return null;
            Check(status);
            if (result == IntPtr.Zero || Native.CFGetTypeID(result) != Native.CFDataGetTypeID())
                throw new CryptographicException("macOS Keychain returned an invalid item.");
            var length = Native.CFDataGetLength(result);
            if (length < 0 || length > MaximumSecretBytes) throw new CryptographicException("macOS Keychain item is too large.");
            var data = new byte[(int)length];
            if (data.Length > 0) Marshal.Copy(Native.CFDataGetBytePtr(result), data, 0, data.Length);
            return data;
        }
        finally { if (result != IntPtr.Zero) Native.CFRelease(result); }
    }

    public bool TryAdd(string service, string account, byte[] data)
    {
        EnsurePlatform();
        using var query = Query(service, account, suppressPrompts: false);
        query.SetData(data);
        var status = Native.SecItemAdd(query.Handle, IntPtr.Zero);
        if (status == Duplicate) return false;
        Check(status);
        return true;
    }

    public void Write(string service, string account, byte[] data)
    {
        EnsurePlatform();
        using var query = Query(service, account);
        using var attributes = new DictionaryHandle();
        attributes.SetData(data);
        var status = Native.SecItemUpdate(query.Handle, attributes.Handle);
        if (status == NotFound)
        {
            if (TryAdd(service, account, data)) return;
            status = Native.SecItemUpdate(query.Handle, attributes.Handle);
        }
        Check(status);
    }

    public void Delete(string service, string account)
    {
        EnsurePlatform();
        using var query = Query(service, account);
        var status = Native.SecItemDelete(query.Handle);
        if (status != NotFound) Check(status);
    }

    private static DictionaryHandle Query(string service, string account, bool suppressPrompts = true)
    {
        var query = new DictionaryHandle();
        try
        {
            query.Set(Native.SecurityConstant("kSecClass"), Native.SecurityConstant("kSecClassGenericPassword"));
            query.SetString("kSecAttrService", service);
            query.SetString("kSecAttrAccount", account);
            // Report locked/denied access instead of blocking startup on a system prompt.
            if (suppressPrompts)
                query.Set(Native.SecurityConstant("kSecUseAuthenticationUI"), Native.SecurityConstant("kSecUseAuthenticationUIFail"));
            return query;
        }
        catch { query.Dispose(); throw; }
    }

    private static void Check(int status)
    {
        if (status != 0) throw new MacKeychainException(status);
    }

    private static void EnsurePlatform()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS Keychain is unavailable.");
    }

    private sealed class DictionaryHandle : IDisposable
    {
        internal IntPtr Handle { get; } = Native.CFDictionaryCreateMutable(IntPtr.Zero, 0,
            Native.CoreExport("kCFTypeDictionaryKeyCallBacks"), Native.CoreExport("kCFTypeDictionaryValueCallBacks"));
        internal DictionaryHandle()
        {
            if (Handle == IntPtr.Zero) throw new CryptographicException("Could not create a Keychain query.");
        }
        internal void Set(IntPtr key, IntPtr value) => Native.CFDictionarySetValue(Handle, key, value);
        internal void SetString(string key, string value)
        {
            var text = Native.CFStringCreateWithCharacters(IntPtr.Zero, value, value.Length);
            if (text == IntPtr.Zero) throw new CryptographicException("Could not create a Keychain attribute.");
            try { Set(Native.SecurityConstant(key), text); }
            finally { Native.CFRelease(text); }
        }
        internal void SetData(byte[] data)
        {
            if (data.Length > MaximumSecretBytes) throw new CryptographicException("Keychain item is too large.");
            var value = Native.CFDataCreate(IntPtr.Zero, data, data.Length);
            if (value == IntPtr.Zero) throw new CryptographicException("Could not create Keychain data.");
            try { Set(Native.SecurityConstant("kSecValueData"), value); }
            finally { Native.CFRelease(value); }
        }
        public void Dispose() => Native.CFRelease(Handle);
    }

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security);
        private static readonly IntPtr CoreLibrary = NativeLibrary.Load(Core);
        internal static IntPtr SecurityConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityLibrary, name));
        internal static IntPtr CoreExport(string name) => NativeLibrary.GetExport(CoreLibrary, name);
        internal static IntPtr CoreConstant(string name) => Marshal.ReadIntPtr(CoreExport(name));

        [DllImport(Security)] internal static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(Security)] internal static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(Security)] internal static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
        [DllImport(Security)] internal static extern int SecItemDelete(IntPtr query);
        [DllImport(Core)] internal static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
        [DllImport(Core)] internal static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
        [DllImport(Core, CharSet = CharSet.Unicode)] internal static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, string text, nint length);
        [DllImport(Core)] internal static extern IntPtr CFDataCreate(IntPtr allocator, byte[] data, nint length);
        [DllImport(Core)] internal static extern nint CFDataGetLength(IntPtr data);
        [DllImport(Core)] internal static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(Core)] internal static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(Core)] internal static extern nuint CFDataGetTypeID();
        [DllImport(Core)] internal static extern void CFRelease(IntPtr value);
    }
}

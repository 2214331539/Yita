using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Yita.Core.Settings;

namespace Yita.Native.Windows;

/// <summary>
/// Stores Yita's API key in the current Windows user's Credential Manager.
/// The key never enters settings.json, diagnostics, or process arguments.
/// </summary>
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const string TargetName = "Yita:DeepSeekApiKey";
    private const uint GenericCredentialType = 1;
    private const uint LocalMachinePersistence = 2;
    private const uint ErrorNotFound = 1168;

    public Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<string?>(null);

        return Task.FromResult(ReadCredential());
    }

    public Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Credential Manager is unavailable.");

        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            DeleteCredential();
            return Task.CompletedTask;
        }

        var targetPointer = IntPtr.Zero;
        var blobPointer = IntPtr.Zero;
        try
        {
            var blob = Encoding.UTF8.GetBytes(normalized);
            if (blob.Length > ushort.MaxValue)
                throw new ArgumentException("The API key is too long.", nameof(value));

            targetPointer = Marshal.StringToCoTaskMemUni(TargetName);
            blobPointer = Marshal.AllocHGlobal(blob.Length);
            Marshal.Copy(blob, 0, blobPointer, blob.Length);

            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = LocalMachinePersistence,
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows 凭据。");
        }
        finally
        {
            if (blobPointer != IntPtr.Zero) Marshal.FreeHGlobal(blobPointer);
            if (targetPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPointer);
        }

        return Task.CompletedTask;
    }

    private static string? ReadCredential()
    {
        if (!CredRead(TargetName, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            var size = checked((int)credential.CredentialBlobSize);
            var blob = new byte[size];
            Marshal.Copy(credential.CredentialBlob, blob, 0, size);
            return Encoding.UTF8.GetString(blob).Trim();
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static void DeleteCredential()
    {
        if (CredDelete(TargetName, GenericCredentialType, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "无法删除 Windows 凭据。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        internal uint Flags;
        internal uint Type;
        internal IntPtr TargetName;
        internal IntPtr Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal uint CredentialBlobSize;
        internal IntPtr CredentialBlob;
        internal uint Persist;
        internal uint AttributeCount;
        internal IntPtr Attributes;
        internal IntPtr TargetAlias;
        internal IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string targetName,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}

public static class SecretStoreFactory
{
    public static ISecretStore CreateDefault() => OperatingSystem.IsWindows()
        ? new WindowsCredentialSecretStore()
        : new MemorySecretStore();
}

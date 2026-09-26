using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Yita.Settings;

internal interface IApiKeyStore
{
    string ReadApiKey();

    void SaveApiKey(string apiKey);
}

internal sealed class WindowsCredentialStore : IApiKeyStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetName = "Yita/DeepSeekApiKey";

    public string ReadApiKey()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return string.Empty;
            }

            throw new Win32Exception(error, "无法从 Windows 凭据管理器读取 API Key。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(
                       credential.CredentialBlob,
                       checked((int)credential.CredentialBlobSize / sizeof(char)))
                   ?? string.Empty;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            DeleteApiKey();
            return;
        }

        var secretBytes = Encoding.Unicode.GetBytes(apiKey);
        if (secretBytes.Length > 2560)
        {
            throw new ArgumentOutOfRangeException(nameof(apiKey), "API Key 超出 Windows 凭据管理器允许的长度。");
        }

        var blobPointer = Marshal.AllocCoTaskMem(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blobPointer, secretBytes.Length);
            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = blobPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将 API Key 保存到 Windows 凭据管理器。");
            }
        }
        finally
        {
            Marshal.Copy(new byte[secretBytes.Length], 0, blobPointer, secretBytes.Length);
            Marshal.FreeCoTaskMem(blobPointer);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static void DeleteApiKey()
    {
        if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法从 Windows 凭据管理器删除 API Key。");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

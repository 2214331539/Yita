using System.Security.Cryptography;
using System.Text;
using Yita.Translation;

namespace Yita.Native.Windows;

internal sealed class WindowsTranslationMemoryProtector : ITranslationMemoryProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Yita/translation-memory/v1");

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}

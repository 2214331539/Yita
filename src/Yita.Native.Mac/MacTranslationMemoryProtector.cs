using System.Security.Cryptography;
using Yita.Core.Platform;
using Yita.Translation;

namespace Yita.Native.Mac;

internal static class MacTranslationMemoryProtector
{
    internal const string KeyAccount = "translation-memory-key-v1";

    internal static Task<ITranslationMemoryProtector> CreateAsync(bool allowCreate,
        CancellationToken cancellationToken = default, IMacKeychainAccess? keychain = null) => Task.Run<ITranslationMemoryProtector>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = keychain ?? new MacKeychainAccess();
        var key = store.Read(MacKeychainSecretStore.Service, KeyAccount);
        if (key is null && allowCreate)
        {
            var candidate = RandomNumberGenerator.GetBytes(32);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (store.TryAdd(MacKeychainSecretStore.Service, KeyAccount, candidate)) key = candidate.ToArray();
                else key = store.Read(MacKeychainSecretStore.Service, KeyAccount);
            }
            finally { CryptographicOperations.ZeroMemory(candidate); }
        }
        if (key is null) throw new CryptographicException("The saved translation memory key is missing; existing data has been preserved.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new AesGcmTranslationMemoryProtector(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }, cancellationToken);
}

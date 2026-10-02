using System.Security.Cryptography;
using System.Text;
using Yita.Core.Settings;

namespace Yita.Native.Mac;

/// <summary>Uses Security.framework directly; secrets never enter a command line or subprocess.</summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    internal const string Service = "com.yita.Yita";
    internal const string ApiKeyAccount = "deepseek-api-key";
    private readonly IMacKeychainAccess _keychain;

    public MacKeychainSecretStore() : this(new MacKeychainAccess()) { }
    internal MacKeychainSecretStore(IMacKeychainAccess keychain) => _keychain = keychain;

    public Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = _keychain.Read(Service, ApiKeyAccount);
        if (data is null) return null;
        try { return Encoding.UTF8.GetString(data).Trim(); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }, cancellationToken);

    public Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) { _keychain.Delete(Service, ApiKeyAccount); return; }
        var data = Encoding.UTF8.GetBytes(normalized);
        try { _keychain.Write(Service, ApiKeyAccount, data); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }, cancellationToken);
}

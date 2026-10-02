using System.Security.Cryptography;
using System.Text;
using Yita.Core.Platform;
using Yita.Translation;

namespace Yita.Native.Mac.Tests;

public sealed class KeychainStorageTests
{
    [Fact]
    public async Task ApiKeyIsTrimmedUpdatedAndDeletedWithoutTouchingTheMemoryKey()
    {
        var keychain = new FakeKeychain();
        var memoryKey = RandomNumberGenerator.GetBytes(32);
        keychain.Items[MacTranslationMemoryProtector.KeyAccount] = memoryKey.ToArray();
        var store = new MacKeychainSecretStore(keychain);
        Assert.Null(await store.ReadApiKeyAsync());
        await store.SaveApiKeyAsync(" test-api-key ");
        Assert.Equal("test-api-key", await store.ReadApiKeyAsync());
        await store.SaveApiKeyAsync("replacement");
        Assert.Equal("replacement", await store.ReadApiKeyAsync());
        await store.SaveApiKeyAsync("");
        await store.SaveApiKeyAsync("");
        Assert.Null(await store.ReadApiKeyAsync());
        Assert.Equal(memoryKey, keychain.Items[MacTranslationMemoryProtector.KeyAccount]);
    }

    [Fact]
    public async Task LockedKeychainErrorsAreNotReportedAsMissingCredentials()
    {
        var keychain = new FakeKeychain { Failure = new MacKeychainException(-25308) };
        var store = new MacKeychainSecretStore(keychain);
        Assert.Equal(-25308, (await Assert.ThrowsAsync<MacKeychainException>(() => store.ReadApiKeyAsync())).Status);
        await Assert.ThrowsAsync<MacKeychainException>(() => store.SaveApiKeyAsync("test-key"));
        await Assert.ThrowsAsync<MacKeychainException>(() => MacTranslationMemoryProtector.CreateAsync(true, keychain: keychain));
        Assert.Empty(keychain.Items);
    }

    [Fact]
    public async Task CancellationBeforeStartingDoesNotTouchKeychain()
    {
        var keychain = new FakeKeychain();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new MacKeychainSecretStore(keychain);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadApiKeyAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveApiKeyAsync("test-key", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MacTranslationMemoryProtector.CreateAsync(true, cancelled.Token, keychain));
        Assert.Equal(0, keychain.Calls);
    }

    [Fact]
    public async Task EncryptedCorrectionsAreReadableAfterReopeningAndNeverStoredAsPlaintext()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "memory.dat");
            var keychain = new FakeKeychain();
            using var first = (AesGcmTranslationMemoryProtector)await MacTranslationMemoryProtector.CreateAsync(true, keychain: keychain);
            var memory = new TranslationMemoryStore(path, first);
            memory.AddOrUpdate("private source text", "private corrected text", "en", "zh");
            var encrypted = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain("private source text", Encoding.UTF8.GetString(encrypted));
            Assert.DoesNotContain("private corrected text", Encoding.UTF8.GetString(encrypted));
            using var second = (AesGcmTranslationMemoryProtector)await MacTranslationMemoryProtector.CreateAsync(false, keychain: keychain);
            var reopened = new TranslationMemoryStore(path, second);
            Assert.False(reopened.LoadFailed);
            Assert.Equal("private corrected text", reopened.FindExact("private source text", "en", "zh")!.TargetText);
            Assert.Equal(1, keychain.AddAttempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingOrInvalidEncryptionKeyIsNotRegeneratedForExistingRecords()
    {
        var keychain = new FakeKeychain();
        await Assert.ThrowsAsync<CryptographicException>(() => MacTranslationMemoryProtector.CreateAsync(false, keychain: keychain));
        Assert.Equal(0, keychain.AddAttempts);
        keychain.Items[MacTranslationMemoryProtector.KeyAccount] = new byte[8];
        await Assert.ThrowsAsync<CryptographicException>(() => MacTranslationMemoryProtector.CreateAsync(true, keychain: keychain));
        Assert.Equal(0, keychain.AddAttempts);
        Assert.Equal(8, keychain.Items[MacTranslationMemoryProtector.KeyAccount].Length);
    }

    [Fact]
    public async Task ConcurrentCreationUsesTheWinningKeyWithoutReplacingIt()
    {
        var winner = RandomNumberGenerator.GetBytes(32);
        var keychain = new FakeKeychain { RacingKey = winner };
        using var created = (AesGcmTranslationMemoryProtector)await MacTranslationMemoryProtector.CreateAsync(true, keychain: keychain);
        using var expected = new AesGcmTranslationMemoryProtector(winner);
        var plaintext = Encoding.UTF8.GetBytes("concurrent record");
        Assert.Equal(plaintext, expected.Unprotect(created.Protect(plaintext)));
        Assert.Equal(winner, keychain.Items[MacTranslationMemoryProtector.KeyAccount]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(21)]
    [InlineData(37)]
    public void TamperedHeaderNonceTagOrCiphertextFailsAuthentication(int index)
    {
        using var protector = new AesGcmTranslationMemoryProtector(RandomNumberGenerator.GetBytes(32));
        var encrypted = protector.Protect(Encoding.UTF8.GetBytes("private correction"));
        encrypted[index] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(encrypted));
    }

    [Fact]
    public void DifferentKeysTruncatedDataAndDisposedProtectorsCannotDecrypt()
    {
        using var first = new AesGcmTranslationMemoryProtector(RandomNumberGenerator.GetBytes(32));
        using var second = new AesGcmTranslationMemoryProtector(RandomNumberGenerator.GetBytes(32));
        var plaintext = Encoding.UTF8.GetBytes("private correction");
        var encrypted = first.Protect(plaintext);
        Assert.NotEqual(encrypted, first.Protect(plaintext));
        Assert.Equal(plaintext, first.Unprotect(encrypted));
        Assert.ThrowsAny<CryptographicException>(() => second.Unprotect(encrypted));
        Assert.ThrowsAny<CryptographicException>(() => first.Unprotect(encrypted[..20]));
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first.Protect(plaintext));
        Assert.Throws<ObjectDisposedException>(() => first.Unprotect(encrypted));
    }

    [Fact]
    public void UnreadableMemoryRejectsSavingAndPreservesTheFile()
    {
        var directory = TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "memory.dat");
            using var protector = new AesGcmTranslationMemoryProtector(RandomNumberGenerator.GetBytes(32));
            new TranslationMemoryStore(path, protector).AddOrUpdate("source", "target", "en", "zh");
            var damaged = File.ReadAllBytes(path);
            damaged[^1] ^= 1;
            File.WriteAllBytes(path, damaged);
            var reopened = new TranslationMemoryStore(path, protector);
            Assert.True(reopened.LoadFailed);
            Assert.Throws<InvalidDataException>(() => reopened.AddOrUpdate("new source", "new target", "en", "zh"));
            Assert.Equal(damaged, File.ReadAllBytes(path));
            reopened.Clear();
            reopened.AddOrUpdate("new source", "new target", "en", "zh");
            Assert.Equal(1, reopened.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeKeychainIsNeverLoadedOnWindows()
    {
        if (OperatingSystem.IsMacOS()) return;
        var store = new MacKeychainSecretStore();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.ReadApiKeyAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => store.SaveApiKeyAsync("test-key"));
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "yita-keychain-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeKeychain : IMacKeychainAccess
    {
        internal Dictionary<string, byte[]> Items { get; } = new();
        internal Exception? Failure { get; init; }
        internal byte[]? RacingKey { get; init; }
        internal int Calls { get; private set; }
        internal int AddAttempts { get; private set; }
        public byte[]? Read(string service, string account)
        {
            Check(service);
            return Items.TryGetValue(account, out var data) ? data.ToArray() : null;
        }
        public bool TryAdd(string service, string account, byte[] data)
        {
            Check(service);
            AddAttempts++;
            if (RacingKey is not null) { Items[account] = RacingKey.ToArray(); return false; }
            return Items.TryAdd(account, data.ToArray());
        }
        public void Write(string service, string account, byte[] data) { Check(service); Items[account] = data.ToArray(); }
        public void Delete(string service, string account) { Check(service); Items.Remove(account); }
        private void Check(string service)
        {
            Calls++;
            Assert.Equal(MacKeychainSecretStore.Service, service);
            if (Failure is not null) throw Failure;
        }
    }
}

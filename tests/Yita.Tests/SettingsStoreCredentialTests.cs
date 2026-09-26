using Yita.Settings;
using System.IO;

namespace Yita.Tests;

public sealed class SettingsStoreCredentialTests
{
    [Fact]
    public void TransientCredentialReadFailureCanRecoverWithoutLosingKey()
    {
        var credentialStore = new StubApiKeyStore
        {
            ApiKey = "stored-secret",
            ThrowOnRead = true,
        };
        var settingsStore = new SettingsStore(
            credentialStore,
            Path.Combine(Path.GetTempPath(), $"Yita-{Guid.NewGuid():N}.json"));

        var initial = settingsStore.Load();

        Assert.Empty(initial.DeepSeekApiKey);
        Assert.True(settingsStore.ApiKeyReadFailed);

        credentialStore.ThrowOnRead = false;
        Assert.True(settingsStore.TryReadApiKey(out var recoveredKey));
        Assert.Equal("stored-secret", recoveredKey);
        Assert.False(settingsStore.ApiKeyReadFailed);
    }

    [Fact]
    public void SavingPreferencesCanPreserveCredentialAfterReadFailure()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"Yita-{Guid.NewGuid():N}.json");
        var credentialStore = new StubApiKeyStore
        {
            ApiKey = "stored-secret",
            ThrowOnRead = true,
        };
        var settingsStore = new SettingsStore(credentialStore, settingsPath);
        _ = settingsStore.Load();

        try
        {
            settingsStore.Save(
                AppSettings.Default with { DeepSeekApiKey = string.Empty },
                persistApiKey: false);

            Assert.Equal(0, credentialStore.SaveCallCount);
            Assert.Equal("stored-secret", credentialStore.ApiKey);
            Assert.True(settingsStore.ApiKeyReadFailed);
        }
        finally
        {
            File.Delete(settingsPath);
        }
    }

    [Fact]
    public void LoadingPreferencesDoesNotTouchCredentialManager()
    {
        var credentialStore = new StubApiKeyStore
        {
            ThrowOnRead = true,
        };
        var settingsStore = new SettingsStore(
            credentialStore,
            Path.Combine(Path.GetTempPath(), $"Yita-{Guid.NewGuid():N}.json"));

        var preferences = settingsStore.LoadPreferences();

        Assert.Equal("deepseek", preferences.ProviderId);
        Assert.Empty(preferences.DeepSeekApiKey);
        Assert.Equal(0, credentialStore.ReadCallCount);
        Assert.False(settingsStore.ApiKeyReadFailed);
    }

    private sealed class StubApiKeyStore : IApiKeyStore
    {
        public string ApiKey { get; set; } = string.Empty;

        public bool ThrowOnRead { get; set; }

        public int SaveCallCount { get; private set; }

        public int ReadCallCount { get; private set; }

        public string ReadApiKey()
        {
            ReadCallCount++;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated credential service failure.");
            }

            return ApiKey;
        }

        public void SaveApiKey(string apiKey)
        {
            SaveCallCount++;
            ApiKey = apiKey;
        }
    }
}

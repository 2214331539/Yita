using System.Text.Json;
using Yita.Core.Settings;

namespace Yita.Core.Tests;

public sealed class SettingsSchemaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yita-schema-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "desktop-settings.json");

    public SettingsSchemaTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LegacySettingsAreBackedUpOnlyWhenTheFirstMigrationIsSaved()
    {
        const string legacy = "{\"UiLanguage\":\"zh-CN\",\"PopupOffsetX\":-24.5,\"PopupOffsetY\":36,\"UseClipboardFallback\":false}";
        await File.WriteAllTextAsync(SettingsPath, legacy);
        var store = new JsonSettingsStore(SettingsPath);
        var migrated = await store.LoadAsync();
        Assert.Equal(YitaSettings.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal("zh-CN", migrated.UiLanguage);
        Assert.Equal(-24.5, migrated.PopupOffsetX);
        Assert.Equal(36, migrated.PopupOffsetY);
        Assert.True(migrated.UseClipboardFallback);
        Assert.Equal(legacy, await File.ReadAllTextAsync(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".schema-0.bak"));
        await store.SaveAsync(migrated with { UseClipboardFallback = false });
        Assert.Equal(legacy, await File.ReadAllTextAsync(SettingsPath + ".schema-0.bak"));
        Assert.False((await store.LoadAsync()).UseClipboardFallback);
        await store.SaveAsync(migrated);
        Assert.Equal(legacy, await File.ReadAllTextAsync(SettingsPath + ".schema-0.bak"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}", SettingsFailureKind.NewerVersion)]
    [InlineData("{\"SchemaVersion\":999,\"unknownFutureField\":true}", SettingsFailureKind.NewerVersion)]
    [InlineData("{", SettingsFailureKind.InvalidContent)]
    [InlineData("null", SettingsFailureKind.InvalidContent)]
    [InlineData("[]", SettingsFailureKind.InvalidContent)]
    [InlineData("{\"schemaVersion\":\"1\"}", SettingsFailureKind.InvalidContent)]
    [InlineData("{\"schemaVersion\":-1}", SettingsFailureKind.InvalidContent)]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":1}", SettingsFailureKind.InvalidContent)]
    [InlineData("{\"schemaVersion\":1,\"isEnabled\":\"true\"}", SettingsFailureKind.InvalidContent)]
    public async Task UnreadableOrFutureSettingsCannotBeOverwritten(string contents, SettingsFailureKind expected)
    {
        await File.WriteAllTextAsync(SettingsPath, contents);
        var store = new JsonSettingsStore(SettingsPath);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync())).Kind);
        Assert.Equal(expected, (await Assert.ThrowsAsync<SettingsStoreException>(() => store.SaveAsync(YitaSettings.Default))).Kind);
        Assert.Equal(contents, await File.ReadAllTextAsync(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task SaveRechecksTheOnDiskVersionAfterAnEarlierSuccessfulLoad()
    {
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(YitaSettings.Default);
        var settings = await store.LoadAsync();
        const string future = "{\"schemaVersion\":99,\"newFeature\":42}";
        await File.WriteAllTextAsync(SettingsPath, future);
        var exception = await Assert.ThrowsAsync<SettingsStoreException>(() => store.SaveAsync(settings));
        Assert.Equal(SettingsFailureKind.NewerVersion, exception.Kind);
        Assert.Equal(future, await File.ReadAllTextAsync(SettingsPath));
    }

    [Fact]
    public async Task CancelledSaveLeavesTheOriginalSettingsAndNoTemporaryFiles()
    {
        var store = new JsonSettingsStore(SettingsPath);
        await store.SaveAsync(YitaSettings.Default);
        var bytes = await File.ReadAllBytesAsync(SettingsPath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            YitaSettings.Default with { UiLanguage = "zh-CN" }, cancelled.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingSettingsReturnDefaultsButAnInvalidFileDoesNotImportTheFallback()
    {
        var fallback = Path.Combine(_directory, "legacy.json");
        await File.WriteAllTextAsync(fallback, "{\"UiLanguage\":\"zh-CN\",\"StartWithWindows\":true,\"AiHistoryEnabled\":true}");
        Assert.Equal(YitaSettings.Default, await new JsonSettingsStore(SettingsPath).LoadAsync());
        var store = new JsonSettingsStore(SettingsPath, fallback);
        var imported = await store.LoadAsync();
        Assert.Equal("zh-CN", imported.UiLanguage);
        Assert.False(imported.StartWithSystem);
        Assert.False(imported.AiHistoryEnabled);
        await File.WriteAllTextAsync(SettingsPath, "broken");
        await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task OversizedFileIsRejectedAndNeverReplaced()
    {
        await File.WriteAllTextAsync(SettingsPath, new string(' ', 1024 * 1024 + 1));
        var store = new JsonSettingsStore(SettingsPath);
        Assert.Equal(SettingsFailureKind.InvalidContent, (await Assert.ThrowsAsync<SettingsStoreException>(() => store.LoadAsync())).Kind);
        await Assert.ThrowsAsync<SettingsStoreException>(() => store.SaveAsync(YitaSettings.Default));
        Assert.Equal(1024 * 1024 + 1, new FileInfo(SettingsPath).Length);
    }

    [Fact]
    public async Task ConcurrentSavesProduceOneCompleteCurrentSchemaFile()
    {
        var store = new JsonSettingsStore(SettingsPath);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => store.SaveAsync(
            YitaSettings.Default with { PersonalGlossary = "term => " + index })));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(SettingsPath));
        Assert.Equal(YitaSettings.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith("term => ", (await store.LoadAsync()).PersonalGlossary);
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

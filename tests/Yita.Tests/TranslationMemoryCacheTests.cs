using Yita.Translation;

namespace Yita.Tests;

public sealed class TranslationMemoryCacheTests
{
    [Fact]
    public void Defaults_AreBoundedForDesktopUse()
    {
        Assert.Equal(128, TranslationMemoryCache.DefaultMaxEntries);
        Assert.Equal(1024 * 1024, TranslationMemoryCache.DefaultMaxCharacterCount);
        Assert.Equal(TimeSpan.FromMinutes(20), TranslationMemoryCache.DefaultTimeToLive);
    }

    [Fact]
    public void TryGet_MovesEntryToMostRecentlyUsedPosition()
    {
        var cache = new TranslationMemoryCache(maxEntries: 2);
        var first = CreateKey("first");
        var second = CreateKey("second");
        var third = CreateKey("third");
        cache.Set(first, "一");
        cache.Set(second, "二");

        Assert.True(cache.TryGet(first, out _));
        cache.Set(third, "三");

        Assert.True(cache.TryGet(first, out var firstValue));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out var thirdValue));
        Assert.Equal("一", firstValue);
        Assert.Equal("三", thirdValue);
    }

    [Fact]
    public void TryGet_ExpiresEntryAfterTimeToLiveWithoutSlidingOnHit()
    {
        var now = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var cache = new TranslationMemoryCache(
            timeToLive: TimeSpan.FromMinutes(30),
            utcNow: () => now);
        var key = CreateKey("hello");
        cache.Set(key, "你好");

        now = now.AddMinutes(20);
        Assert.True(cache.TryGet(key, out _));

        now = now.AddMinutes(10);
        Assert.False(cache.TryGet(key, out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CharacterCount);
    }

    [Fact]
    public void Set_EvictsLeastRecentlyUsedEntryToHonorCharacterBudget()
    {
        var first = CreateKey("a");
        var second = CreateKey("b");
        var singleEntryCost = CharacterCost(first, "1234567890");
        var cache = new TranslationMemoryCache(
            maxEntries: 10,
            maxCharacterCount: singleEntryCost * 2 - 1);

        cache.Set(first, "1234567890");
        cache.Set(second, "abcdefghij");

        Assert.False(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(second, out _));
        Assert.True(cache.CharacterCount <= singleEntryCost * 2 - 1);
    }

    [Fact]
    public void Set_DoesNotRetainAnEntryLargerThanTheWholeBudget()
    {
        var cache = new TranslationMemoryCache(maxCharacterCount: 32);

        cache.Set(CreateKey("a very long source sentence"), new string('x', 100));

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CharacterCount);
    }

    [Fact]
    public void CacheKey_DistinguishesProviderConfigurationDirectionAndPromptVersion()
    {
        var baseline = TranslationCacheKey.Create(
            "deepseek",
            "https://api.deepseek.com/",
            "deepseek-v4-flash",
            "auto",
            "zh-CN",
            "hello",
            1);

        Assert.NotEqual(baseline, baseline with { ProviderId = "mock" });
        Assert.NotEqual(baseline, baseline with { Endpoint = "https://example.com" });
        Assert.NotEqual(baseline, baseline with { Model = "deepseek-v4-pro" });
        Assert.NotEqual(baseline, baseline with { SourceLanguage = "en" });
        Assert.NotEqual(baseline, baseline with { TargetLanguage = "en" });
        Assert.NotEqual(
            baseline,
            TranslationCacheKey.Create(
                "deepseek",
                "https://api.deepseek.com/",
                "deepseek-v4-flash",
                "auto",
                "zh-CN",
                "world",
                1));
        Assert.NotEqual(baseline, baseline with { PromptVersion = 2 });
        Assert.NotEqual(
            baseline,
            TranslationCacheKey.Create(
                "deepseek",
                "https://api.deepseek.com/",
                "deepseek-v4-flash",
                "auto",
                "zh-CN",
                "hello",
                1,
                "different-context"));
        Assert.Equal("https://api.deepseek.com", baseline.Endpoint);
    }

    [Fact]
    public void CacheKey_DoesNotRetainSourceText()
    {
        const string sensitiveText = "private source text 9f2dc1";

        var key = CreateKey(sensitiveText);

        Assert.Equal(64, key.TextFingerprint.Length);
        Assert.DoesNotContain(sensitiveText, key.TextFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_ReplacesExistingValueAndAccounting()
    {
        var cache = new TranslationMemoryCache();
        var key = CreateKey("hello");
        cache.Set(key, "short");
        var originalCost = cache.CharacterCount;

        cache.Set(key, "a longer replacement");

        Assert.True(cache.TryGet(key, out var translation));
        Assert.Equal("a longer replacement", translation);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.CharacterCount > originalCost);
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_KeepLimitsAndRemainConsistent()
    {
        var cache = new TranslationMemoryCache(maxEntries: 40, maxCharacterCount: 16_000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 250; index++)
            {
                var key = CreateKey($"{worker}-{index % 60}");
                cache.Set(key, $"translated-{index}");
                cache.TryGet(key, out _);
            }
        })));

        Assert.InRange(cache.Count, 1, 40);
        Assert.InRange(cache.CharacterCount, 1, 16_000);
    }

    [Fact]
    public void Clear_RemovesAllEntriesAndAccounting()
    {
        var cache = new TranslationMemoryCache();
        cache.Set(CreateKey("one"), "一");
        cache.Set(CreateKey("two"), "二");

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.CharacterCount);
    }

    private static TranslationCacheKey CreateKey(string text)
    {
        return TranslationCacheKey.Create(
            "deepseek",
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            "auto",
            "zh-CN",
            text,
            1);
    }

    private static int CharacterCost(TranslationCacheKey key, string translation)
    {
        return key.ProviderId.Length
            + key.Endpoint.Length
            + key.Model.Length
            + key.SourceLanguage.Length
            + key.TargetLanguage.Length
            + key.TextFingerprint.Length
            + key.OptionsFingerprint.Length
            + translation.Length;
    }
}

using Yita.Translation;

namespace Yita.Tests;

public sealed class TranslationInFlightRegistryTests
{
    [Fact]
    public async Task IdenticalKey_JoinsExistingCompletionAndRemovesByIdentity()
    {
        var registry = new TranslationInFlightRegistry();
        var key = CreateKey("same");
        var owner = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(registry.TryRegister(key, owner.Task));
        Assert.True(registry.TryJoin(key, out var joined));
        Assert.Same(owner.Task, joined);

        owner.SetResult("result");
        Assert.Equal("result", await joined!);
        Assert.True(registry.TryRemove(key, owner.Task));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DifferentOptions_DoNotCoalesce()
    {
        var registry = new TranslationInFlightRegistry();
        var first = CreateKey("same", "balanced");
        var second = CreateKey("same", "precise");
        var completion = Task.FromResult("result");

        Assert.True(registry.TryRegister(first, completion));

        Assert.False(registry.TryJoin(second, out _));
    }

    private static TranslationCacheKey CreateKey(string text, string options = "") =>
        TranslationCacheKey.Create(
            "deepseek",
            "https://api.deepseek.com",
            "model",
            "auto",
            "zh-CN",
            text,
            3,
            options);
}

using Yita.Translation;
using System.IO;

namespace Yita.Tests;

public sealed class TranslationMemoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"Yita-memory-tests-{Guid.NewGuid():N}");

    [Fact]
    public void AddOrUpdate_PersistsProtectedDataAndRestoresExactMatch()
    {
        var path = Path.Combine(_directory, "memory.dat");
        var protector = new PrefixProtector();
        var store = new TranslationMemoryStore(path, protector);

        store.AddOrUpdate("Hello world", "你好，世界", "English", "Chinese");

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("Hello world", raw, StringComparison.Ordinal);
        var restored = new TranslationMemoryStore(path, protector);
        Assert.Equal("你好，世界", restored.FindExact("Hello world", "English", "Chinese")?.TargetText);
    }

    [Fact]
    public void AddOrUpdate_ReplacesSameDirectionAndKeepsOppositeDirection()
    {
        var store = CreateStore();
        store.AddOrUpdate("bank", "银行", "en", "zh");
        store.AddOrUpdate("bank", "河岸", "en", "zh");
        store.AddOrUpdate("bank", "bank", "zh", "en");

        Assert.Equal(2, store.Count);
        Assert.Equal("河岸", store.FindExact("bank", "en", "zh")?.TargetText);
        Assert.Equal("bank", store.FindExact("bank", "zh", "en")?.TargetText);
    }

    [Fact]
    public void FindExact_NormalizesLanguageAliasesAndTreatsAutoSourceAsCompatible()
    {
        var store = CreateStore();
        store.AddOrUpdate("Hello", "你好", "English", "Simplified Chinese");

        Assert.Equal("你好", store.FindExact("Hello", "英语", "zh-CN")?.TargetText);
        Assert.Equal("你好", store.FindExact("Hello", "自动检测", "简体中文")?.TargetText);
        Assert.Null(store.FindExact("Hello", "auto", "英语"));
    }

    [Fact]
    public void FindRelevant_RanksSimilarExamplesAndExcludesUnrelatedOrWrongDirection()
    {
        var store = CreateStore();
        store.AddOrUpdate(
            "The finite element model converged quickly.",
            "有限元模型快速收敛。",
            "en",
            "zh");
        store.AddOrUpdate(
            "The weather is sunny today.",
            "今天天气晴朗。",
            "en",
            "zh");
        store.AddOrUpdate(
            "The finite element solver converged.",
            "The finite element solver converged.",
            "zh",
            "en");

        var result = store.FindRelevant(
            "A finite element model should converge.",
            "en",
            "zh");

        var example = Assert.Single(result);
        Assert.Contains("finite element", example.SourceText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Clear_RemovesEntriesAndEncryptedFile()
    {
        var path = Path.Combine(_directory, "memory.dat");
        var store = new TranslationMemoryStore(path, new PrefixProtector());
        store.AddOrUpdate("one", "一", "en", "zh");

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AddOrUpdate_WhenPersistenceFails_RollsBackMemoryAndLeavesFileUntouched()
    {
        var path = Path.Combine(_directory, "memory.dat");
        var protector = new SwitchableProtector();
        var store = new TranslationMemoryStore(path, protector);
        store.AddOrUpdate("old", "旧", "en", "zh");
        var originalFile = File.ReadAllBytes(path);
        protector.ThrowOnProtect = true;

        Assert.Throws<InvalidOperationException>(
            () => store.AddOrUpdate("new", "新", "en", "zh"));

        Assert.Equal(1, store.Count);
        Assert.Equal("旧", store.FindExact("old", "en", "zh")?.TargetText);
        Assert.Null(store.FindExact("new", "en", "zh"));
        Assert.Equal(originalFile, File.ReadAllBytes(path));
    }

    [Fact]
    public void CorruptExistingMemoryIsNeverOverwrittenByANewCorrection()
    {
        var path = Path.Combine(_directory, "corrupt-memory.dat");
        var protector = new PrefixProtector();
        Directory.CreateDirectory(_directory);
        var corruptFile = protector.Protect("not-json"u8.ToArray());
        File.WriteAllBytes(path, corruptFile);

        var store = new TranslationMemoryStore(path, protector);

        Assert.True(store.LoadFailed);
        Assert.Throws<InvalidDataException>(
            () => store.AddOrUpdate("new", "新", "en", "zh"));
        Assert.Equal(corruptFile, File.ReadAllBytes(path));
    }

    [Fact]
    public void Load_IgnoresNullEntriesWithoutChangingTheOriginalFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "null-entry.dat");
        var protector = new PrefixProtector();
        var original = protector.Protect("{\"Version\":1,\"Entries\":[null]}"u8.ToArray());
        File.WriteAllBytes(path, original);
        var store = new TranslationMemoryStore(path, protector);
        Assert.False(store.LoadFailed);
        Assert.Equal(0, store.Count);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void AddOrUpdate_RejectsUnreadablyLargeFilesAndRollsBack()
    {
        var path = Path.Combine(_directory, "oversize.dat");
        var protector = new SwitchableProtector();
        var store = new TranslationMemoryStore(path, protector);
        store.AddOrUpdate("old", "旧", "en", "zh");
        var original = File.ReadAllBytes(path);
        protector.ReturnOversizedData = true;

        Assert.Throws<InvalidDataException>(() => store.AddOrUpdate("new", "新", "en", "zh"));
        Assert.Equal(1, store.Count);
        Assert.Equal("旧", store.FindExact("old", "en", "zh")?.TargetText);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("same text", "same text", 1.0)]
    [InlineData("finite element analysis", "finite element model", 0.5)]
    [InlineData("finite element analysis", "sunny weather", 0.0)]
    public void CalculateSimilarity_IsLanguageAgnosticAndBounded(
        string first,
        string second,
        double minimum)
    {
        var similarity = TranslationMemoryStore.CalculateSimilarity(first, second);

        Assert.InRange(similarity, minimum, 1.0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private TranslationMemoryStore CreateStore() =>
        new(Path.Combine(_directory, $"{Guid.NewGuid():N}.dat"), new PrefixProtector());

    private sealed class PrefixProtector : ITranslationMemoryProtector
    {
        private static readonly byte[] Prefix = "protected:"u8.ToArray();

        public byte[] Protect(byte[] plaintext) => [.. Prefix, .. plaintext.Select(value => (byte)(value ^ 0x5A))];

        public byte[] Unprotect(byte[] protectedData)
        {
            Assert.True(protectedData.AsSpan().StartsWith(Prefix));
            return protectedData[Prefix.Length..].Select(value => (byte)(value ^ 0x5A)).ToArray();
        }
    }

    private sealed class SwitchableProtector : ITranslationMemoryProtector
    {
        public bool ThrowOnProtect { get; set; }
        public bool ReturnOversizedData { get; set; }

        public byte[] Protect(byte[] plaintext)
        {
            if (ReturnOversizedData) return new byte[TranslationMemoryStore.MaximumFileBytes + 1];
            if (ThrowOnProtect)
            {
                throw new InvalidOperationException("Simulated persistence failure.");
            }

            return plaintext.Select(value => (byte)(value ^ 0xA5)).ToArray();
        }

        public byte[] Unprotect(byte[] protectedData) =>
            protectedData.Select(value => (byte)(value ^ 0xA5)).ToArray();
    }
}

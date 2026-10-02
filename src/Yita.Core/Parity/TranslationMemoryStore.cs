using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Yita.Translation;

internal sealed record SavedTranslation(
    string SourceText,
    string TargetText,
    string SourceLanguage,
    string TargetLanguage,
    DateTimeOffset SavedAt);

internal interface ITranslationMemoryProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Stores only corrections the user explicitly saves. The file is protected
/// with Windows DPAPI for the current account and written atomically.
/// </summary>
internal sealed class TranslationMemoryStore
{
    internal const int MaximumEntries = 200;
    internal const int MaximumSourceLength = 8_000;
    internal const int MaximumTargetLength = 20_000;
    internal const int DefaultRelevantLimit = 3;
    internal const int MaximumRelevantCharacterBudget = 2_400;
    internal const long MaximumFileBytes = 4 * 1024 * 1024;
    internal const double MinimumRelevantSimilarity = 0.24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly object _syncRoot = new();
    private readonly string _path;
    private readonly ITranslationMemoryProtector _protector;
    private readonly List<SavedTranslation> _entries;
    private bool _loadFailed;

    public TranslationMemoryStore(
        string? path = null,
        ITranslationMemoryProtector? protector = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yita",
            "translation-memory.dat");
        _protector = protector ?? throw new ArgumentNullException(nameof(protector), "A platform memory protector is required.");
        _entries = Load();
    }

    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _entries.Count;
            }
        }
    }

    internal bool LoadFailed => _loadFailed;

    public SavedTranslation? FindExact(
        string sourceText,
        string sourceLanguage,
        string targetLanguage)
    {
        var normalizedSource = NormalizeText(sourceText);
        lock (_syncRoot)
        {
            return _entries.FirstOrDefault(entry =>
                string.Equals(entry.SourceText, normalizedSource, StringComparison.Ordinal)
                && SameDirection(entry, sourceLanguage, targetLanguage));
        }
    }

    public IReadOnlyList<SavedTranslation> FindRelevant(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        int limit = DefaultRelevantLimit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var normalizedSource = NormalizeText(sourceText);
        if (normalizedSource.Length == 0)
        {
            return [];
        }

        SavedTranslation[] candidates;
        lock (_syncRoot)
        {
            candidates = _entries
                .Where(entry => SameDirection(entry, sourceLanguage, targetLanguage)
                    && !string.Equals(entry.SourceText, normalizedSource, StringComparison.Ordinal))
                .ToArray();
        }

        var normalizedForSimilarity = NormalizeForSimilarity(normalizedSource);
        var sourceBigrams = CreateBigrams(normalizedForSimilarity);
        var effectiveLimit = Math.Min(limit, DefaultRelevantLimit);
        var ranked = candidates
            .Select(entry => new
            {
                Entry = entry,
                Similarity = CalculateSimilarity(
                    normalizedForSimilarity,
                    sourceBigrams,
                    entry.SourceText),
            })
            .Where(candidate => candidate.Similarity >= MinimumRelevantSimilarity)
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenByDescending(candidate => candidate.Entry.SavedAt);

        var result = new List<SavedTranslation>(effectiveLimit);
        var characterCount = 0;
        foreach (var candidate in ranked)
        {
            if (result.Count >= effectiveLimit)
            {
                break;
            }

            var entryCost = candidate.Entry.SourceText.Length + candidate.Entry.TargetText.Length;
            if (entryCost > MaximumRelevantCharacterBudget - characterCount)
            {
                continue;
            }

            result.Add(candidate.Entry);
            characterCount += entryCost;
        }

        return result;
    }

    public void AddOrUpdate(
        string sourceText,
        string targetText,
        string sourceLanguage,
        string targetLanguage)
    {
        var source = NormalizeText(sourceText);
        var target = NormalizeText(targetText);
        if (source.Length == 0
            || target.Length == 0
            || source.Length > MaximumSourceLength
            || target.Length > MaximumTargetLength)
        {
            throw new ArgumentException("The saved translation is empty or exceeds the allowed length.");
        }

        var sourceDirection = NormalizeLanguage(sourceLanguage);
        var targetDirection = NormalizeLanguage(targetLanguage);
        lock (_syncRoot)
        {
            if (_loadFailed)
            {
                throw new InvalidDataException(
                    "Translation memory could not be read; refusing to overwrite the existing file.");
            }

            var previousEntries = _entries.ToArray();
            try
            {
                _entries.RemoveAll(entry =>
                    string.Equals(entry.SourceText, source, StringComparison.Ordinal)
                    && SameDirection(entry, sourceDirection, targetDirection));
                _entries.Insert(0, new SavedTranslation(
                    source,
                    target,
                    sourceDirection,
                    targetDirection,
                    DateTimeOffset.UtcNow));
                if (_entries.Count > MaximumEntries)
                {
                    _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
                }

                SaveLocked();
            }
            catch
            {
                _entries.Clear();
                _entries.AddRange(previousEntries);
                throw;
            }
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            _entries.Clear();
            _loadFailed = false;
        }
    }

    internal static double CalculateSimilarity(string first, string second)
    {
        var left = NormalizeForSimilarity(first);
        return CalculateSimilarity(left, CreateBigrams(left), second);
    }

    private static double CalculateSimilarity(
        string normalizedFirst,
        IReadOnlySet<int> firstBigrams,
        string second)
    {
        var right = NormalizeForSimilarity(second);
        if (normalizedFirst.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(normalizedFirst, right, StringComparison.Ordinal))
        {
            return 1;
        }

        var rightBigrams = CreateBigrams(right);
        if (firstBigrams.Count == 0 || rightBigrams.Count == 0)
        {
            return normalizedFirst.Contains(right, StringComparison.Ordinal)
                || right.Contains(normalizedFirst, StringComparison.Ordinal)
                ? 0.5
                : 0;
        }

        var intersection = firstBigrams.Count(rightBigrams.Contains);
        var dice = 2d * intersection / (firstBigrams.Count + rightBigrams.Count);
        var lengthRatio = (double)Math.Min(normalizedFirst.Length, right.Length)
            / Math.Max(normalizedFirst.Length, right.Length);
        return dice * (0.75 + 0.25 * lengthRatio);
    }

    private static HashSet<int> CreateBigrams(string value)
    {
        var result = new HashSet<int>();
        for (var index = 0; index < value.Length - 1; index++)
        {
            result.Add((value[index] << 16) | value[index + 1]);
        }

        return result;
    }

    private List<SavedTranslation> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var fileLength = new FileInfo(_path).Length;
            if (fileLength is <= 0 or > MaximumFileBytes)
            {
                throw new InvalidDataException("Translation memory file has an invalid size.");
            }

            var protectedData = File.ReadAllBytes(_path);
            var plaintext = _protector.Unprotect(protectedData);
            try
            {
                var document = JsonSerializer.Deserialize<TranslationMemoryDocument>(plaintext, JsonOptions);
                if (document?.Version != 1 || document.Entries is null)
                {
                    _loadFailed = true;
                    return [];
                }

                return document.Entries
                    .Where(IsValidEntry)
                    .Take(MaximumEntries)
                    .ToList();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException)
        {
            _loadFailed = true;
            return [];
        }
        catch (JsonException)
        {
            _loadFailed = true;
            return [];
        }
        catch (IOException)
        {
            _loadFailed = true;
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            _loadFailed = true;
            return [];
        }
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Translation memory path is invalid.");
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new TranslationMemoryDocument(1, _entries),
            JsonOptions);
        byte[] protectedData;
        try
        {
            protectedData = _protector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        if (protectedData.LongLength is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException("Translation memory exceeds the readable file size limit.");
        }
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool SameDirection(
        SavedTranslation entry,
        string sourceLanguage,
        string targetLanguage)
    {
        var entrySource = NormalizeLanguage(entry.SourceLanguage);
        var requestedSource = NormalizeLanguage(sourceLanguage);
        var sourceMatches = entrySource == "auto"
            || requestedSource == "auto"
            || string.Equals(entrySource, requestedSource, StringComparison.OrdinalIgnoreCase);
        return sourceMatches
            && string.Equals(
                NormalizeLanguage(entry.TargetLanguage),
                NormalizeLanguage(targetLanguage),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidEntry(SavedTranslation? entry)
    {
        return entry is not null
            && !string.IsNullOrWhiteSpace(entry.SourceText)
            && !string.IsNullOrWhiteSpace(entry.TargetText)
            && entry.SourceText.Length <= MaximumSourceLength
            && entry.TargetText.Length <= MaximumTargetLength
            && !string.IsNullOrWhiteSpace(entry.SourceLanguage)
            && !string.IsNullOrWhiteSpace(entry.TargetLanguage);
    }

    private static string NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static string NormalizeLanguage(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "auto"
            : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "auto" or "auto detect" or "自动" or "自动检测" or "自动判断" => "auto",
            "en" or "en-us" or "en-gb" or "english" or "英语" or "英文" => "en",
            "zh" or "zh-cn" or "zh-hans" or "chinese" or "simplified chinese" or "中文" or "简体中文" => "zh-CN",
            "ja" or "ja-jp" or "japanese" or "日语" or "日文" => "ja",
            _ => normalized,
        };
    }

    private static string NormalizeForSimilarity(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || IsCjk(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u9FFF';

    private sealed record TranslationMemoryDocument(
        int Version,
        IReadOnlyList<SavedTranslation> Entries);
}

namespace Yita.Translation;

/// <summary>
/// Coalesces small streaming deltas while keeping the first visible content
/// immediate. Create one instance per translation stream.
/// </summary>
internal sealed class StreamingUpdateThrottle
{
    internal static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds(35);
    internal const int DefaultMinimumCharacterDelta = 32;

    private readonly TimeSpan _minimumInterval;
    private readonly int _minimumCharacterDelta;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _hasPublished;
    private int _lastPublishedLength;
    private DateTimeOffset _lastPublishedAt;

    public StreamingUpdateThrottle(
        TimeSpan? minimumInterval = null,
        int minimumCharacterDelta = DefaultMinimumCharacterDelta,
        Func<DateTimeOffset>? utcNow = null)
    {
        var effectiveMinimumInterval = minimumInterval ?? DefaultMinimumInterval;
        if (effectiveMinimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        if (minimumCharacterDelta <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCharacterDelta));
        }

        _minimumInterval = effectiveMinimumInterval;
        _minimumCharacterDelta = minimumCharacterDelta;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public int LastPublishedLength => _lastPublishedLength;

    public bool HasPendingUpdate(int contentLength)
    {
        ValidateContentLength(contentLength);
        return contentLength > 0 && (!_hasPublished || contentLength != _lastPublishedLength);
    }

    public bool ShouldPublish(int contentLength, bool isFinal = false)
    {
        ValidateContentLength(contentLength);

        if (contentLength == 0)
        {
            return false;
        }

        var now = _utcNow();
        var shouldPublish = isFinal
            || !_hasPublished
            || contentLength < _lastPublishedLength
            || contentLength - _lastPublishedLength >= _minimumCharacterDelta
            || now - _lastPublishedAt >= _minimumInterval;

        if (!isFinal && _hasPublished && contentLength == _lastPublishedLength)
        {
            return false;
        }

        if (!shouldPublish)
        {
            return false;
        }

        _hasPublished = true;
        _lastPublishedLength = contentLength;
        _lastPublishedAt = now;
        return true;
    }

    public void Reset()
    {
        _hasPublished = false;
        _lastPublishedLength = 0;
        _lastPublishedAt = default;
    }

    private static void ValidateContentLength(int contentLength)
    {
        if (contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }
    }
}

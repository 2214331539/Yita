using System.Text;

namespace Yita.Selection;

/// <summary>
/// Collects distinct text fragments without ever exceeding a fixed character
/// budget. The next read limit lets UI Automation avoid retrieving text that
/// would be discarded immediately afterwards.
/// </summary>
internal sealed class BoundedTextAccumulator
{
    private readonly int _maximumLength;
    private readonly string _separator;
    private readonly List<string> _parts = [];
    private readonly HashSet<string> _seen;
    private int _length;

    public BoundedTextAccumulator(
        int maximumLength,
        string? separator = null,
        IEqualityComparer<string>? comparer = null)
    {
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        _maximumLength = maximumLength;
        _separator = separator ?? Environment.NewLine;
        _seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);
    }

    public int MaximumNextPartLength
    {
        get
        {
            var separatorLength = _parts.Count == 0 ? 0 : _separator.Length;
            return Math.Max(0, _maximumLength - _length - separatorLength);
        }
    }

    public bool TryAdd(string? value)
    {
        if (string.IsNullOrEmpty(value) || MaximumNextPartLength == 0)
        {
            return false;
        }

        var boundedValue = value.Length <= MaximumNextPartLength
            ? value
            : value[..MaximumNextPartLength];
        if (!_seen.Add(boundedValue))
        {
            return false;
        }

        if (_parts.Count > 0)
        {
            _length += _separator.Length;
        }

        _parts.Add(boundedValue);
        _length += boundedValue.Length;
        return true;
    }

    public string? Build()
    {
        if (_parts.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder(_length);
        for (var index = 0; index < _parts.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(_separator);
            }

            builder.Append(_parts[index]);
        }

        return builder.ToString();
    }
}

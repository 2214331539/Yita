using System.Buffers;
using System.IO;
using System.Text;

namespace Yita.Translation;

/// <summary>Reads UTF-8 SSE lines without allowing a single line to grow unbounded.</summary>
internal sealed class BoundedSseLineReader(Stream stream, int maximumLineBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _position;
    private int _count;
    private bool _skipLineFeed;
    private bool _firstLine = true;

    internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte>? pending = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == _count)
            {
                _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_count == 0)
                    return pending is { WrittenCount: > 0 } ? Decode(pending.WrittenSpan) : null;
            }
            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (_buffer[_position] == (byte)'\n')
                {
                    _position++;
                    continue;
                }
            }
            var end = _position;
            while (end < _count && _buffer[end] is not (byte)'\n' and not (byte)'\r') end++;
            var length = end - _position;
            if ((pending?.WrittenCount ?? 0) + length > maximumLineBytes)
                throw new TranslationProviderException("DeepSeek 流式数据行过长，已停止读取。", TranslationFailureKind.Protocol);
            if (end < _count)
            {
                var start = _position;
                _skipLineFeed = _buffer[end] == (byte)'\r';
                _position = end + 1;
                if (pending is null) return Decode(_buffer.AsSpan(start, length));
                pending.Write(_buffer.AsSpan(start, length));
                return Decode(pending.WrittenSpan);
            }
            pending ??= new ArrayBufferWriter<byte>();
            pending.Write(_buffer.AsSpan(_position, length));
            _position = end;
        }
    }

    private string Decode(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (_firstLine)
        {
            _firstLine = false;
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        }
        return text;
    }
}

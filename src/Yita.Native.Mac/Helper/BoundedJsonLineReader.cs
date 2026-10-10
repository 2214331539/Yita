namespace Yita.Native.Mac;

internal sealed class BoundedJsonLineReader(Stream stream, int maximumBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;

    internal async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0) throw new EndOfStreamException("Native helper disconnected.");
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            var length = (newline < 0 ? _count : newline) - _offset;
            if (line.Length + length > maximumBytes) throw new IOException("Native helper response exceeded the limit.");
            line.Write(_buffer, _offset, length);
            _offset += length;
            if (newline >= 0) { _offset++; return line.ToArray(); }
        }
    }
}

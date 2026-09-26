using System.IO;
using System.Text;
using Yita.Translation;

namespace Yita.Tests;

public sealed class BoundedSseLineReaderTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task ReadsUtf8BomAndEmptyLinesAcrossSmallNetworkReads(string separator)
    {
        using var stream = new SmallReadStream(Encoding.UTF8.GetBytes("\uFEFF中文" + separator + separator + "尾行"));
        var reader = new BoundedSseLineReader(stream, 32);
        Assert.Equal("中文", await reader.ReadLineAsync(default));
        Assert.Equal("", await reader.ReadLineAsync(default));
        Assert.Equal("尾行", await reader.ReadLineAsync(default));
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public async Task EnforcesLimitEvenWithoutTrailingNewline(int length, bool valid)
    {
        using var stream = new SmallReadStream(Encoding.UTF8.GetBytes(new string('x', length)));
        var reader = new BoundedSseLineReader(stream, 8);
        if (valid) Assert.Equal(new string('x', length), await reader.ReadLineAsync(default));
        else await Assert.ThrowsAsync<TranslationProviderException>(async () => await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task HonorsCancellationBeforeReadingBufferedContent()
    {
        using var stream = new MemoryStream("one\ntwo\n"u8.ToArray());
        var reader = new BoundedSseLineReader(stream, 32);
        Assert.Equal("one", await reader.ReadLineAsync(default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadLineAsync(cancellation.Token));
    }

    private sealed class SmallReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }
}

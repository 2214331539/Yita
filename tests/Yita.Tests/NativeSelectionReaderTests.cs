using Yita.Selection;

namespace Yita.Tests;

public sealed class NativeSelectionReaderTests
{
    [Theory]
    [InlineData("Edit")]
    [InlineData("RichEdit20W")]
    [InlineData("RICHEDIT50W")]
    [InlineData("WindowsForms10.EDIT.app.0.1.abc")]
    public void RecognizesNativeEditControls(string className)
    {
        Assert.True(NativeSelectionReader.IsEditControl(className));
    }

    [Theory]
    [InlineData("Scintilla")]
    [InlineData("scintilla")]
    public void RecognizesScintillaControls(string className)
    {
        Assert.True(NativeSelectionReader.IsScintillaControl(className));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chrome_RenderWidgetHostHWND")]
    public void RejectsUnsupportedControlClasses(string? className)
    {
        Assert.False(NativeSelectionReader.IsEditControl(className));
        Assert.False(NativeSelectionReader.IsScintillaControl(className));
    }

    [Theory]
    [InlineData(0x00000020, true)]
    [InlineData(0x50010020, true)]
    [InlineData(0x50010000, false)]
    [InlineData(0, false)]
    public void DetectsPasswordEditStyle(long style, bool expected)
    {
        Assert.Equal(expected, NativeSelectionReader.IsPasswordStyle(style));
    }

    [Theory]
    [InlineData(0, 100_000, 80_000, true, 20_000, 20_001)]
    [InlineData(79_000, 90_000, 100_000, true, 90_000, 90_001)]
    [InlineData(1_990_000, 2_030_000, 2_000_000, true, 2_010_000, 2_000_001)]
    [InlineData(2_000_000, 2_010_000, 2_000_000, false, 0, 0)]
    [InlineData(-1, 10, 100, false, 0, 0)]
    [InlineData(20, 20, 100, false, 0, 0)]
    public void CalculatesLengthBoundedCrossProcessEditReads(
        int selectionStart,
        int selectionEnd,
        int textLength,
        bool expectedResult,
        int expectedEnd,
        int expectedBufferLength)
    {
        var result = NativeSelectionReader.TryCalculateBoundedRead(
            selectionStart,
            selectionEnd,
            textLength,
            out var requestedEnd,
            out var bufferLength);

        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedEnd, requestedEnd);
        Assert.Equal(expectedBufferLength, bufferLength);
    }
}

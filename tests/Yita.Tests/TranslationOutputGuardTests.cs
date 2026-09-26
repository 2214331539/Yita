using Yita.Translation;

namespace Yita.Tests;

public sealed class TranslationOutputGuardTests
{
    [Theory]
    [InlineData("(Your translation engine decides the language of your output: if both the input text and the requested output language are the same, return the input text as-is.)")]
    [InlineData("([[h1:Your]] translation engine decides the language of your output: return the text.)")]
    [InlineData("You are a translation engine that may only output translated text.")]
    [InlineData("Translate only the value of the JSON field text.")]
    public void RejectsInstructionEchoForUnrelatedAssignment(string response)
    {
        Assert.True(TranslationOutputGuard.IsInstructionEcho("Your project must use at least two sprites.", response));
    }

    [Theory]
    [InlineData("Your project must use at least two sprites.", "你的项目必须使用至少两个角色。")]
    [InlineData("You are a translation engine that may only output translated text.", "You are a translation engine that may only output translated text.")]
    [InlineData("你是一个翻译引擎，只输出译文。", "You are a translation engine. Return only translated text.")]
    [InlineData("int idx = 0;", "int idx = 0;")]
    public void PreservesOrdinaryTranslationsAndLegitimateInstructionSource(string source, string response)
    {
        Assert.False(TranslationOutputGuard.IsInstructionEcho(source, response));
    }
}

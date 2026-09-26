using Yita.Translation;

namespace Yita.Tests;

public sealed class PersonalGlossaryTests
{
    [Fact]
    public void Parse_AcceptsCommonSeparatorsAndSkipsInvalidOrDuplicateLines()
    {
        const string text = """
            # project terms
            bank => 河岸
            API=接口
            model→模型
            API => 重复项
            invalid line
            """;

        var entries = PersonalGlossary.Parse(text);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new GlossaryEntry("bank", "河岸"), entries[0]);
        Assert.Equal(new GlossaryEntry("API", "接口"), entries[1]);
        Assert.Equal(new GlossaryEntry("model", "模型"), entries[2]);
    }

    [Fact]
    public void Parse_CapsEntryCount()
    {
        var text = string.Join('\n', Enumerable.Range(0, 140).Select(index => $"term-{index} => value-{index}"));

        var entries = PersonalGlossary.Parse(text);

        Assert.Equal(PersonalGlossary.MaximumEntries, entries.Count);
    }
}

using Yita.Translation;

namespace Yita.Tests;

public sealed class TranslationProviderFactoryTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", true)]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://api.example.com", false)]
    [InlineData("https://user:secret@example.com", false)]
    [InlineData("https://example.com/#fragment", false)]
    [InlineData("https://example.com/?api-key=secret", false)]
    [InlineData("not-a-url", false)]
    public void TryValidateEndpoint_EnforcesSecureRemoteConnections(string value, bool expected)
    {
        var result = TranslationProviderFactory.TryValidateEndpoint(value, out var endpoint);

        Assert.Equal(expected, result);
        Assert.Equal(expected, endpoint is not null);
    }
}

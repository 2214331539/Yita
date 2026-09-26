using Yita.Settings;

namespace Yita.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void BuildCommand_QuotesExecutablePath()
    {
        var command = StartupRegistration.BuildCommand(@"C:\Apps With Spaces\Yita.exe");

        Assert.Equal("\"C:\\Apps With Spaces\\Yita.exe\"", command);
    }

    [Fact]
    public void BuildCommand_RejectsEmbeddedQuote()
    {
        Assert.Throws<ArgumentException>(() => StartupRegistration.BuildCommand("bad\"path.exe"));
    }
}

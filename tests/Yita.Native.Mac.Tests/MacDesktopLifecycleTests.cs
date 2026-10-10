using System.Xml.Linq;

namespace Yita.Native.Mac.Tests;

public sealed class MacDesktopLifecycleTests
{
    [Theory]
    [InlineData("/Volumes/Yita Preview/Yita.app/Contents/MacOS/Yita.Desktop", MacApplicationLocation.DiskImage)]
    [InlineData("/private/var/folders/test/AppTranslocation/random/d/Yita.app/Contents/MacOS/Yita.Desktop", MacApplicationLocation.Translocated)]
    [InlineData("/Applications/Yita.app/Contents/MacOS/Yita.Desktop", MacApplicationLocation.Applications)]
    [InlineData("/Users/test/Applications/Yita.app/Contents/MacOS/Yita.Desktop", MacApplicationLocation.Applications)]
    [InlineData("/Users/test/Downloads/Yita.app/Contents/MacOS/Yita.Desktop", MacApplicationLocation.OtherDirectory)]
    [InlineData("/usr/local/share/dotnet/dotnet", MacApplicationLocation.Unpackaged)]
    public void InstallationLocationDistinguishesTemporaryLaunchesFromInstalledApps(string executable, MacApplicationLocation location) =>
        Assert.Equal(location, MacApplicationBundle.GetLocation(executable, "/Users/test"));

    [Fact]
    public void LoginAgentUsesSeparateArgumentsAndRunsOnlyAtTheNextGuiLogin()
    {
        using var fixture = new StartupFixture();
        Assert.True(fixture.Service.IsSupported);
        fixture.Service.Apply(true);
        var document = XDocument.Load(fixture.AgentPath);
        var dictionary = document.Root!.Element("dict")!;
        var arguments = dictionary.Elements("key").Single(element => element.Value == "ProgramArguments")
            .ElementsAfterSelf().First();
        Assert.Equal(new[] { fixture.Executable, "--background" }, arguments.Elements().Select(element => element.Value));
        Assert.DoesNotContain("KeepAlive", document.ToString());
        Assert.Contains("Aqua", document.ToString());
        Assert.Contains("AssociatedBundleIdentifiers", document.ToString());
        fixture.Service.Apply(true);
        Assert.Empty(Directory.GetFiles(fixture.AgentDirectory, "*.tmp"));
        fixture.Service.Apply(false);
        Assert.False(File.Exists(fixture.AgentPath));
        Assert.True(File.Exists(fixture.Executable));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("wrong-bundle")]
    [InlineData("wrong-executable")]
    [InlineData("wrong-type")]
    [InlineData("missing-metadata")]
    [InlineData("oversize")]
    public void SourcePreviewsAndInvalidAppMetadataCannotEnableStartup(string mode)
    {
        using var fixture = new StartupFixture();
        if (mode == "missing-metadata") File.Delete(fixture.InfoPath);
        else if (mode == "oversize") File.WriteAllText(fixture.InfoPath, new string('x', 40_000));
        else if (mode != "dotnet")
        {
            var document = XDocument.Load(fixture.InfoPath);
            var key = mode == "wrong-bundle" ? "CFBundleIdentifier" : mode == "wrong-type" ? "CFBundlePackageType" : "CFBundleExecutable";
            document.Root!.Element("dict")!.Elements("key").Single(element => element.Value == key).ElementsAfterSelf().First().Value = "other";
            document.Save(fixture.InfoPath);
        }
        var service = mode == "dotnet" ? new MacStartupRegistration(Path.Combine(fixture.Directory, "dotnet"), fixture.AgentDirectory) : fixture.Service;
        Assert.False(service.IsSupported);
        Assert.Throws<InvalidOperationException>(() => service.Apply(true));
        Assert.False(File.Exists(fixture.AgentPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignOrModifiedAgentIsPreservedWhenEnablingOrDisabling(bool enable)
    {
        using var fixture = new StartupFixture();
        Directory.CreateDirectory(fixture.AgentDirectory);
        var document = MacStartupRegistration.CreatePlist(fixture.Executable);
        document.Root!.Element("dict")!.Add(new XElement("key", "KeepAlive"), new XElement("true"));
        document.Save(fixture.AgentPath);
        var original = File.ReadAllBytes(fixture.AgentPath);
        Assert.Throws<IOException>(() => fixture.Service.Apply(enable));
        Assert.Equal(original, File.ReadAllBytes(fixture.AgentPath));
    }

    [Fact]
    public void MovingAnAppUpdatesOnlyTheOwnedAgentAndDisableSurvivesARemovedApp()
    {
        using var fixture = new StartupFixture();
        fixture.Service.Apply(true);
        var document = XDocument.Load(fixture.AgentPath);
        document.Root!.Element("dict")!.Element("array")!.Elements().First().Value =
            Path.Combine(fixture.Directory, "Previous.app", "Contents", "MacOS", "Yita.Desktop");
        document.Save(fixture.AgentPath);
        fixture.Service.Apply(true);
        Assert.Equal(fixture.Executable, XDocument.Load(fixture.AgentPath).Root!.Element("dict")!.Element("array")!.Elements().First().Value);
        File.Delete(fixture.Executable);
        Assert.False(fixture.Service.IsSupported);
        fixture.Service.Apply(false);
        Assert.False(File.Exists(fixture.AgentPath));
    }

    [Fact]
    public void AppleXmlDoctypeIsAcceptedWithoutResolvingTheExternalDtd()
    {
        using var fixture = new StartupFixture();
        var contents = File.ReadAllText(fixture.InfoPath);
        var offset = contents.IndexOf("<plist", StringComparison.Ordinal);
        File.WriteAllText(fixture.InfoPath, contents.Insert(offset,
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"));
        Assert.True(fixture.Service.IsSupported);
    }

    [Fact]
    public void RegistrationDoesNotTouchFilesOnAnotherPlatform()
    {
        using var fixture = new StartupFixture();
        var service = new MacStartupRegistration(fixture.Executable, fixture.AgentDirectory, () => false);
        Assert.False(service.IsSupported);
        Assert.Throws<PlatformNotSupportedException>(() => service.Apply(false));
        Assert.False(Directory.Exists(fixture.AgentDirectory));
    }

    [Fact]
    public void FloatingWindowFlagsPreserveUnrelatedBehaviorAndAreIdempotent()
    {
        nuint original = (1 << 1) | (1 << 2) | (1 << 5) | (1 << 7) | (1 << 9) | (1 << 4) | (1 << 14);
        var resolved = MacPopupWindowBehavior.ResolveCollectionBehavior(original);
        Assert.Equal((nuint)((1 << 0) | (1 << 3) | (1 << 6) | (1 << 8) | (1 << 4) | (1 << 14)), resolved);
        Assert.Equal(resolved, MacPopupWindowBehavior.ResolveCollectionBehavior(resolved));
        Assert.False(MacPopupWindowBehavior.Apply(0, "NSWindow"));
        Assert.False(MacPopupWindowBehavior.Apply(1, "NSView"));
    }

    [Fact]
    public void LinkedAgentDirectoryCannotRedirectRegistrationWrites()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var fixture = new StartupFixture();
        var target = Path.Combine(fixture.Directory, "other-agent-directory");
        System.IO.Directory.CreateDirectory(target);
        System.IO.Directory.CreateSymbolicLink(fixture.AgentDirectory, target);
        Assert.Throws<IOException>(() => fixture.Service.Apply(true));
        Assert.Empty(System.IO.Directory.GetFiles(target));
    }

    private sealed class StartupFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "yita-login-" + Guid.NewGuid().ToString("N"));
        public string Executable { get; }
        public string InfoPath { get; }
        public string AgentDirectory => Path.Combine(Directory, "agents");
        public string AgentPath => Path.Combine(AgentDirectory, MacStartupRegistration.Label + ".plist");
        public MacStartupRegistration Service { get; }
        public StartupFixture()
        {
            // Spaces and XML metacharacters must be passed as literal path data.
            var contents = Path.Combine(Directory, "Yita & test.app", "Contents");
            System.IO.Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            Executable = Path.Combine(contents, "MacOS", "Yita.Desktop");
            File.WriteAllText(Executable, "fixture");
            if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            InfoPath = Path.Combine(contents, "Info.plist");
            new XDocument(new XElement("plist", new XElement("dict",
                new XElement("key", "CFBundleIdentifier"), new XElement("string", MacStartupRegistration.BundleIdentifier),
                new XElement("key", "CFBundleExecutable"), new XElement("string", "Yita.Desktop"),
                new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL")))).Save(InfoPath);
            Service = new(Executable, AgentDirectory);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

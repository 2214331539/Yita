using System.Xml;
using System.Xml.Linq;
using Yita.Core.Platform;

namespace Yita.Native.Mac;

/// <summary>Registers a packaged app for the next GUI login, without launching a second process.</summary>
public sealed class MacStartupRegistration : IStartupRegistration
{
    internal const string BundleIdentifier = "com.yita.desktop";
    internal const string Label = BundleIdentifier + ".login";
    private const string ExecutableName = "Yita.Desktop";
    private const string OwnerKey = "AssociatedBundleIdentifiers";
    private const int MaximumPlistBytes = 32_768;
    private readonly string? _executable;
    private readonly string _agentDirectory;
    private readonly Func<bool> _isMac;

    public MacStartupRegistration() : this(Environment.ProcessPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"),
        OperatingSystem.IsMacOS) { }

    internal MacStartupRegistration(string? executable, string agentDirectory, Func<bool>? isMac = null)
    {
        _executable = executable;
        _agentDirectory = agentDirectory;
        _isMac = isMac ?? (() => true);
    }

    public bool IsSupported => _isMac() && IsPackagedExecutable(_executable);

    public void Apply(bool enabled)
    {
        if (!_isMac()) throw new PlatformNotSupportedException("macOS login startup is unavailable.");
        if (enabled && !IsSupported)
            throw new InvalidOperationException("Login startup requires an installed Yita.app. Source previews cannot register it.");
        EnsureNoLinks(_agentDirectory);
        var path = Path.Combine(_agentDirectory, Label + ".plist");
        EnsureNoLinks(path);
        if (File.Exists(path) && !IsOwned(ReadPlist(path)))
            throw new IOException("An existing login registration conflicts with Yita. It has been preserved.");
        if (!enabled)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(_agentDirectory);
        var document = CreatePlist(_executable!);
        var temporary = Path.Combine(_agentDirectory, ".yita-login-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                document.Save(stream);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static XDocument CreatePlist(string executable) => new(
        new XDeclaration("1.0", "UTF-8", null),
        new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
            new XElement("key", "Label"), new XElement("string", Label),
            new XElement("key", "ProgramArguments"), new XElement("array",
                new XElement("string", executable), new XElement("string", "--background")),
            new XElement("key", "RunAtLoad"), new XElement("true"),
            new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"),
            new XElement("key", "ProcessType"), new XElement("string", "Interactive"),
            new XElement("key", OwnerKey), new XElement("array", new XElement("string", BundleIdentifier)))));

    private static bool IsPackagedExecutable(string? executable)
    {
        if (executable is null || !Path.IsPathFullyQualified(executable) || !File.Exists(executable)) return false;
        try
        {
            var macOS = Path.GetDirectoryName(executable);
            var contents = macOS is null ? null : Path.GetDirectoryName(macOS);
            var app = contents is null ? null : Path.GetDirectoryName(contents);
            if (Path.GetFileName(executable) != ExecutableName || Path.GetFileName(macOS) != "MacOS"
                || Path.GetFileName(contents) != "Contents" || app?.EndsWith(".app", StringComparison.Ordinal) != true) return false;
            EnsureNoLinks(executable);
            if (OperatingSystem.IsMacOS() && (File.GetUnixFileMode(executable)
                & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) return false;
            var metadataPath = Path.Combine(contents!, "Info.plist");
            EnsureNoLinks(metadataPath);
            var metadata = ReadDictionary(ReadPlist(metadataPath));
            return metadata is not null && Value(metadata, "CFBundleIdentifier") == BundleIdentifier
                && Value(metadata, "CFBundleExecutable") == ExecutableName && Value(metadata, "CFBundlePackageType") == "APPL";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException) { return false; }
    }

    private static XDocument ReadPlist(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumPlistBytes) throw new IOException("Login registration metadata is too large.");
        // Standard Apple XML plists contain a DOCTYPE; never resolve its external URL.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumPlistBytes });
        return XDocument.Load(reader);
    }

    private static Dictionary<string, XElement>? ReadDictionary(XDocument document)
    {
        if (document.Root?.Name != "plist" || document.Root.Elements().Count() != 1
            || document.Root.Element("dict") is not { } dictionary) return null;
        var elements = dictionary.Elements().ToArray();
        if (elements.Length % 2 != 0) return null;
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (var i = 0; i < elements.Length; i += 2)
            if (elements[i].Name != "key" || !result.TryAdd(elements[i].Value, elements[i + 1])) return null;
        return result;
    }

    private static string? Value(Dictionary<string, XElement> dictionary, string key) =>
        dictionary.TryGetValue(key, out var element) && element.Name == "string" ? element.Value : null;

    private static bool IsOwned(XDocument document)
    {
        var dictionary = ReadDictionary(document);
        if (dictionary is null || dictionary.Count != 6 || Value(dictionary, "Label") != Label
            || Value(dictionary, "LimitLoadToSessionType") != "Aqua" || Value(dictionary, "ProcessType") != "Interactive"
            || !dictionary.TryGetValue(OwnerKey, out var owner) || owner.Name != "array"
            || owner.Elements().Count() != 1 || owner.Element("string")?.Value != BundleIdentifier
            || !dictionary.TryGetValue("RunAtLoad", out var run) || run.Name != "true"
            || !dictionary.TryGetValue("ProgramArguments", out var arguments) || arguments.Name != "array") return false;
        var items = arguments.Elements().ToArray();
        return items.Length == 2 && items.All(item => item.Name == "string") && items[1].Value == "--background"
            && Path.IsPathFullyQualified(items[0].Value) && Path.GetFileName(items[0].Value) == ExecutableName
            && Path.GetFileName(Path.GetDirectoryName(items[0].Value)) == "MacOS"
            && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(items[0].Value))) == "Contents"
            && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(items[0].Value)))?.EndsWith(".app", StringComparison.Ordinal) == true;
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Yita login registration cannot use symbolic links.");
    }
}

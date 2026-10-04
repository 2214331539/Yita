namespace Yita.Native.Mac;

public enum MacApplicationLocation { Unpackaged, Applications, DiskImage, Translocated, OtherDirectory }

public static class MacApplicationBundle
{
    public static MacApplicationLocation GetLocation(string? executable, string? userHome = null)
    {
        const string entry = "/Contents/MacOS/Yita.Desktop";
        var path = executable?.Replace('\\', '/');
        if (path is null || !path.EndsWith(entry, StringComparison.Ordinal)) return MacApplicationLocation.Unpackaged;
        var bundle = path[..^entry.Length];
        if (!bundle.EndsWith(".app", StringComparison.Ordinal)) return MacApplicationLocation.Unpackaged;
        if (bundle.Contains("/AppTranslocation/", StringComparison.Ordinal)) return MacApplicationLocation.Translocated;
        if (bundle.StartsWith("/Volumes/", StringComparison.Ordinal)) return MacApplicationLocation.DiskImage;
        var userApplications = userHome?.Replace('\\', '/').TrimEnd('/') + "/Applications/";
        return bundle.StartsWith("/Applications/", StringComparison.Ordinal)
            || bundle.StartsWith("/System/Volumes/Data/Applications/", StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(userHome) && bundle.StartsWith(userApplications, StringComparison.Ordinal))
            ? MacApplicationLocation.Applications : MacApplicationLocation.OtherDirectory;
    }
}

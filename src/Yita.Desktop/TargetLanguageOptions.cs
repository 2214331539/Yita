using Avalonia.Controls;

namespace Yita.Desktop;

internal static class TargetLanguageOptions
{
    internal const string Automatic = "自动判断";
    internal static readonly (string Value, string English, string Chinese)[] Labels =
    [
        (Automatic, "Choose automatically", Automatic),
        ("简体中文", "Simplified Chinese", "简体中文"),
        ("英语", "English", "英语"),
        ("日语", "Japanese", "日语"),
    ];

    internal static ComboBoxItem[] CreateItems() => Labels.Select(option =>
        new ComboBoxItem { Tag = option.Value, Content = option.English }).ToArray();

    internal static string Choice(string mode, string language) =>
        mode == "auto" || !Labels.Any(option => option.Value == language) ? Automatic : language;

    internal static string Label(string value, bool chinese, bool compact = false) => chinese ? value :
        compact && value == Automatic ? "Auto" :
        compact && value == "简体中文" ? "Chinese" :
        Labels.First(option => option.Value == value).English;
}

using Yita.Core.Translation;

namespace Yita.Desktop;

public sealed record HistoryListItem(TranslationHistoryEntry Entry)
{
    public string Summary => Entry.SourceText.Replace('\n', ' ').Replace('\r', ' ');
    public string Details => $"{Entry.CreatedAt.ToLocalTime():MM-dd HH:mm} · {Entry.TargetLanguage}";
}

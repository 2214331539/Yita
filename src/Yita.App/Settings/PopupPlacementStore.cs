using System.IO;
using System.Text.Json;
using Yita.Services;
using Yita.Windows;

namespace Yita.Settings;

internal sealed class PopupPlacementStore
{
    private readonly string _path;
    internal PopupPlacementStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yita", "popup-placement.json");

    internal PopupPlacement? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var value = JsonSerializer.Deserialize<PopupPlacement>(File.ReadAllText(_path));
            return value is { IsValid: true } ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal void Save(PopupPlacement value)
    {
        if (!value.IsValid) return;
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            new RuntimeHealthJournal().Record(RuntimeHealthEvent.PlacementSaveFailed, e);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

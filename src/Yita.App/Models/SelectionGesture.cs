namespace Yita.Models;

internal readonly record struct SelectionGesture(
    ScreenPoint Start,
    ScreenPoint End,
    DateTimeOffset CompletedAt)
{
    // Stable lower-left anchor for the drag region, independent of drag direction.
    // End remains the hit-test location used by selection readers.
    internal ScreenPoint PopupAnchor => new(Math.Min(Start.X, End.X), Math.Max(Start.Y, End.Y));
}

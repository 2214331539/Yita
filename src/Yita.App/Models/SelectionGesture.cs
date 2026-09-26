namespace Yita.Models;

internal readonly record struct SelectionGesture(
    ScreenPoint Start,
    ScreenPoint End,
    DateTimeOffset CompletedAt);

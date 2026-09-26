using Yita.Models;

namespace Yita.Hooks;

internal sealed class SelectionGestureDetector
{
    private readonly int _minimumHorizontalDistance;
    private readonly int _minimumVerticalDistance;
    private ScreenPoint _start;
    private bool _isPressed;

    public SelectionGestureDetector(int minimumHorizontalDistance, int minimumVerticalDistance)
    {
        _minimumHorizontalDistance = Math.Max(1, minimumHorizontalDistance);
        _minimumVerticalDistance = Math.Max(1, minimumVerticalDistance);
    }

    public void Press(ScreenPoint point)
    {
        _start = point;
        _isPressed = true;
    }

    public SelectionGesture? Release(ScreenPoint point, DateTimeOffset completedAt)
    {
        if (!_isPressed)
        {
            return null;
        }

        _isPressed = false;
        var horizontalDistance = Math.Abs(point.X - _start.X);
        var verticalDistance = Math.Abs(point.Y - _start.Y);
        if (horizontalDistance < _minimumHorizontalDistance
            && verticalDistance < _minimumVerticalDistance)
        {
            return null;
        }

        return new SelectionGesture(_start, point, completedAt);
    }

    public void Cancel() => _isPressed = false;
}

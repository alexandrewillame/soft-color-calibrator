using System.Windows;
using System.Windows.Media;
using SoftCal.Core.Analysis;

namespace SoftCal.App;

/// <summary>
/// A level over the camera's view: a circle at the middle of the picture and a dot that shows where the camera points
/// (<see cref="PlacementView.Level"/>). Straight, the dot is green and inside the circle; tilted, it is amber and sits
/// toward the side of the camera to press toward the screen, and it moves back as the user does. It lies over an
/// <c>Image</c> with <c>Stretch="Uniform"</c> in the same cell, sized to the picture as shown.
/// </summary>
internal sealed class TiltLevel : FrameworkElement
{
    /// <summary>The circle's radius, as a fraction of the shown picture's smaller side.</summary>
    private const double CircleSize = 0.15;

    /// <summary>The dot stops this many circle radii from the middle: far enough to say which way, still on the picture.</summary>
    private const double MaxDistance = 2.6;

    private static readonly Brush StraightFill = Frozen(Color.FromRgb(0x45, 0xC4, 0x6F));
    private static readonly Brush TiltedFill = Frozen(Color.FromRgb(0xF2, 0xB2, 0x4C));
    private static readonly Pen Ring = FrozenPen(Color.FromArgb(235, 255, 255, 255), 2);
    private static readonly Pen Halo = FrozenPen(Color.FromArgb(150, 0, 0, 0), 4);
    private static readonly Pen Outline = FrozenPen(Color.FromArgb(220, 0, 0, 0), 1.5);

    private Size _picture;
    private Vector? _dot;
    private bool _straight;

    public TiltLevel() => IsHitTestVisible = false;

    /// <summary>The latest picture's size and what the check says about it.</summary>
    public void Show(int pictureWidth, int pictureHeight, PlacementView view)
    {
        _picture = new Size(pictureWidth, pictureHeight);
        _dot = view.Level is { } level ? new Vector(level.X, level.Y) : null;
        _straight = view.Square == true;
        InvalidateVisual();
    }

    public void Clear()
    {
        _dot = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_dot is not { } dot || _picture.Width <= 0 || _picture.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // The picture as the Image shows it: scaled to fit, centered.
        var scale = Math.Min(ActualWidth / _picture.Width, ActualHeight / _picture.Height);
        var (width, height) = (_picture.Width * scale, _picture.Height * scale);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = CircleSize * Math.Min(width, height);
        var dotRadius = Math.Max(4, radius * 0.3);

        var offset = (dot.Length > MaxDistance ? dot * (MaxDistance / dot.Length) : dot) * radius;
        var (xLimit, yLimit) = ((width / 2) - dotRadius - 2, (height / 2) - dotRadius - 2);
        offset = new Vector(Math.Clamp(offset.X, -xLimit, xLimit), Math.Clamp(offset.Y, -yLimit, yLimit));

        drawingContext.DrawEllipse(null, Halo, center, radius, radius);
        drawingContext.DrawEllipse(null, Ring, center, radius, radius);
        drawingContext.DrawEllipse(_straight ? StraightFill : TiltedFill, Outline, center + offset, dotRadius, dotRadius);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ShortcutWheel.Views;

public class DropIndicatorAdorner : Adorner
{
    public enum DropPosition
    {
        Before,
        After,
        Into
    }

    private readonly DropPosition _position;
    private static readonly Brush GoldBrush;
    private static readonly Pen GoldLinePen;
    private static readonly Pen GoldRectPen;
    private static readonly Brush GoldFillBrush;

    static DropIndicatorAdorner()
    {
        GoldBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x4E));
        GoldBrush.Freeze();

        GoldLinePen = new Pen(GoldBrush, 2.5);
        GoldLinePen.Freeze();

        GoldRectPen = new Pen(GoldBrush, 2);
        GoldRectPen.Freeze();

        GoldFillBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xC9, 0xA2, 0x4E));
        GoldFillBrush.Freeze();
    }

    public DropIndicatorAdorner(UIElement adornedElement, DropPosition position)
        : base(adornedElement)
    {
        _position = position;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = AdornedElement.RenderSize;

        switch (_position)
        {
            case DropPosition.Before:
                DrawDropLine(dc, 0);
                break;
            case DropPosition.After:
                DrawDropLine(dc, size.Height);
                break;
            case DropPosition.Into:
                dc.DrawRoundedRectangle(GoldFillBrush, GoldRectPen,
                    new Rect(0, 0, size.Width, size.Height), 3, 3);
                break;
        }
    }

    private void DrawDropLine(DrawingContext dc, double y)
    {
        double width = AdornedElement.RenderSize.Width;
        // Small triangular cap on the left so the line clearly looks like
        // an "insert here" marker rather than a generic underline.
        const double capSize = 4;
        var triangle = new StreamGeometry();
        using (var ctx = triangle.Open())
        {
            ctx.BeginFigure(new Point(0, y - capSize), true, true);
            ctx.LineTo(new Point(capSize * 1.5, y), true, false);
            ctx.LineTo(new Point(0, y + capSize), true, false);
        }
        triangle.Freeze();
        dc.DrawGeometry(GoldBrush, null, triangle);
        dc.DrawLine(GoldLinePen, new Point(capSize * 1.5, y), new Point(width, y));
    }
}

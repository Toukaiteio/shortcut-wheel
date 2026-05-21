using System.Windows;
using System.Windows.Media;

namespace ShortcutWheel.Controls;

public static class WedgeGeometry
{
    public static StreamGeometry CreateWedge(Point center, double innerRadius, double outerRadius,
        double startAngle, double endAngle)
    {
        var geometry = new StreamGeometry();
        geometry.FillRule = FillRule.EvenOdd;

        using (var ctx = geometry.Open())
        {
            Point outerStart = PolarToCartesian(center, outerRadius, startAngle);
            Point outerEnd = PolarToCartesian(center, outerRadius, endAngle);
            Point innerStart = PolarToCartesian(center, innerRadius, startAngle);
            Point innerEnd = PolarToCartesian(center, innerRadius, endAngle);

            bool largeArc = (endAngle - startAngle) > 180;

            ctx.BeginFigure(outerStart, true, true);
            ctx.ArcTo(outerEnd, new Size(outerRadius, outerRadius), 0, largeArc, SweepDirection.Clockwise, true, false);
            ctx.LineTo(innerEnd, true, false);
            ctx.ArcTo(innerStart, new Size(innerRadius, innerRadius), 0, largeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    public static Point PolarToCartesian(Point center, double radius, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Cos(rad),
            center.Y + radius * Math.Sin(rad));
    }

    /// <summary>
    /// Determines which wedge (if any) contains the given mouse position.
    /// Returns -2 for a hit on the centre disc, -1 for a miss outside the wheel,
    /// or the wedge index in [0, itemCount).
    ///
    /// IMPORTANT: wedges are drawn so that wedge 0 is centred at the top
    /// (math angle -90°). The rotation therefore needs to include an extra
    /// +wedgeAngle/2 so that angle 0 in the rotated frame falls at the start
    /// of wedge 0, not its centre.
    /// </summary>
    public static (int index, double distance) HitTest(Point center, double innerRadius, double outerRadius,
        Point mousePos, int itemCount)
    {
        double dx = mousePos.X - center.X;
        double dy = mousePos.Y - center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < innerRadius)
            return (-2, distance); // Hit centre
        if (distance > outerRadius)
            return (-1, distance); // Miss

        if (itemCount == 0)
            return (-1, distance);

        double wedgeAngle = 360.0 / itemCount;

        // atan2: 0° = right, 90° = down, -90° = up.
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

        // Rotate so the TOP of the screen (math -90°) maps to the *start*
        // of wedge 0 (i.e. 0° in the rotated frame). That means rotating by
        // +90° + wedgeAngle/2.
        angle = (angle + 90 + wedgeAngle / 2 + 360) % 360;

        int index = (int)(angle / wedgeAngle);
        return (Math.Clamp(index, 0, itemCount - 1), distance);
    }
}

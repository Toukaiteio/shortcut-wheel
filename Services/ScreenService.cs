using System.Windows;

namespace ShortcutWheel.Services;

public class ScreenService
{
    public System.Windows.Point ClampToScreenBounds(System.Windows.Point desiredTopLeft, System.Windows.Size wheelSize, double dpiScale = 1.0)
    {
        int w = (int)(wheelSize.Width * dpiScale);
        int h = (int)(wheelSize.Height * dpiScale);
        int cx = (int)(desiredTopLeft.X * dpiScale) + w / 2;
        int cy = (int)(desiredTopLeft.Y * dpiScale) + h / 2;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy));
        var bounds = screen.WorkingArea;

        const int margin = 10;
        int left = cx - w / 2;
        int top = cy - h / 2;

        left = Math.Max(bounds.Left + margin, Math.Min(left, bounds.Right - w - margin));
        top = Math.Max(bounds.Top + margin, Math.Min(top, bounds.Bottom - h - margin));

        return new System.Windows.Point(left / dpiScale, top / dpiScale);
    }
}

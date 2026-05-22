using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ShortcutWheel.Models;
using ShortcutWheel.Services;

namespace ShortcutWheel.Controls;

/// <summary>
/// CSGO-inspired radial menu:
///   • Dark, near-black wedges with subtle radial gradient
///   • Warm gold trim (#C9A24E) for borders, separators, numerals, centre
///   • Hover wedge fades to a gold tint with a brighter outer rim
///   • Always renders a complete disc – when the menu has no items a
///     6-wedge placeholder is drawn so the user knows where to drop things
/// </summary>
public class RadialMenu : FrameworkElement
{
    private const int PlaceholderSlots = 6;
    private const int MinWedgeSlots = 4;

    #region Dependency Properties

    public static readonly DependencyProperty WheelRadiusProperty =
        DependencyProperty.Register(nameof(WheelRadius), typeof(double), typeof(RadialMenu),
            new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CenterRadiusProperty =
        DependencyProperty.Register(nameof(CenterRadius), typeof(double), typeof(RadialMenu),
            new FrameworkPropertyMetadata(70.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(RadialMenu),
            new FrameworkPropertyMetadata(Color.FromRgb(0x1F, 0x1B, 0x17), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverColorProperty =
        DependencyProperty.Register(nameof(HoverColor), typeof(Color), typeof(RadialMenu),
            new FrameworkPropertyMetadata(Color.FromRgb(0x7A, 0x6A, 0x48), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundColorProperty =
        DependencyProperty.Register(nameof(BackgroundColor), typeof(Color), typeof(RadialMenu),
            new FrameworkPropertyMetadata(Color.FromArgb(0xE0, 0x14, 0x13, 0x12), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrimColorProperty =
        DependencyProperty.Register(nameof(TrimColor), typeof(Color), typeof(RadialMenu),
            new FrameworkPropertyMetadata(Color.FromRgb(0xC9, 0xA2, 0x4E), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextColorProperty =
        DependencyProperty.Register(nameof(TextColor), typeof(Color), typeof(RadialMenu),
            new FrameworkPropertyMetadata(Color.FromRgb(0xF2, 0xEC, 0xDD), FrameworkPropertyMetadataOptions.AffectsRender));

    #endregion

    #region Properties

    public double WheelRadius
    {
        get => (double)GetValue(WheelRadiusProperty);
        set => SetValue(WheelRadiusProperty, value);
    }

    public double CenterRadius
    {
        get => (double)GetValue(CenterRadiusProperty);
        set => SetValue(CenterRadiusProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public Color HoverColor
    {
        get => (Color)GetValue(HoverColorProperty);
        set => SetValue(HoverColorProperty, value);
    }

    public Color BackgroundColor
    {
        get => (Color)GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public Color TrimColor
    {
        get => (Color)GetValue(TrimColorProperty);
        set => SetValue(TrimColorProperty, value);
    }

    public Color TextColor
    {
        get => (Color)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public bool IsSubMenu { get; set; }
    public bool HasBackgroundImage { get; set; }

    /// <summary>
    /// Optional "n / m" page indicator drawn beneath the centre logo.
    /// Set to null/empty to hide.
    /// </summary>
    public string? PageInfo { get; set; }

    #endregion

    #region Fields

    private List<ShortcutItem> _items = new();
    private int _hoveredIndex = -1;
    private double _animationProgress = 1.0;
    private bool _isAnimating;

    public double AnimationProgress => _animationProgress;

    public event EventHandler<int>? WedgeClicked;
    public event EventHandler? CenterClicked;

    #endregion

    public RadialMenu()
    {
        SnapsToDevicePixels = true;
        Focusable = true;
    }

    public void SetItems(IList<ShortcutItem> items)
    {
        _items = items.ToList();
        // Pre-warm icon cache so OnRender never triggers a load itself.
        IconExtractor.Prefetch(_items.Select(i => i.TargetPath));
        // Re-evaluate hover based on current mouse position.
        var mousePos = Mouse.GetPosition(this);
        _hoveredIndex = HitTestWedge(mousePos);
        InvalidateVisual();
    }

    public void AnimateIn()
    {
        _animationProgress = 0.0;
        _isAnimating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void AnimateOut()
    {
        _animationProgress = 1.0;
        _isAnimating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isAnimating) return;

        _animationProgress += 0.06; // ~16ms * 16 = 250ms total
        if (_animationProgress >= 1.0)
        {
            _animationProgress = 1.0;
            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        Point center = new Point(WheelRadius, WheelRadius);

        // Clamp so the wedges always have a non-negative thickness, even on
        // the very first animation frame (progress = 0). Without this, the
        // RadialGradientBrush builds fine but `new Rect(..., negative, negative)`
        // when drawing icons throws ArgumentException and aborts the wedge
        // loop, leaving only the first wedge visible.
        double rawRadius = WheelRadius * EaseOutCubic(_animationProgress);
        double animRadius = Math.Max(CenterRadius + 1, rawRadius);

        // 1) Outer disc backdrop — skip when a background image is showing
        //    so the image is visible through the (semi-transparent) wedges.
        if (!HasBackgroundImage)
            DrawBackdrop(dc, center, animRadius);

        // 2) Wedges (real or placeholder)
        bool hasItems = _items.Count > 0;
        int wedgeCount = hasItems ? Math.Max(MinWedgeSlots, _items.Count) : PlaceholderSlots;

        DrawWedges(dc, center, animRadius, wedgeCount, hasItems);

        // 3) Outer ring on top of wedges so the trim looks crisp
        DrawOuterRing(dc, center, animRadius);

        // 4) Centre disc with logo / hint
        DrawCenterCircle(dc, center, hasItems);

        // 5) Empty-state hint floating below the centre
        if (!hasItems)
        {
            DrawEmptyHint(dc, center, animRadius);
        }
    }

    private void DrawBackdrop(DrawingContext dc, Point center, double r)
    {
        var radial = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        radial.GradientStops.Add(new GradientStop(Lighten(BackgroundColor, 0.15), 0.0));
        radial.GradientStops.Add(new GradientStop(BackgroundColor, 0.7));
        radial.GradientStops.Add(new GradientStop(Darken(BackgroundColor, 0.2), 1.0));
        radial.Freeze();

        dc.DrawEllipse(radial, null, center, r, r);
    }

    private void DrawWedges(DrawingContext dc, Point center, double animRadius, int count, bool hasItems)
    {
        // Slightly inset stroke so lines don't disappear behind the outer ring
        var separatorPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)), 1.0);
        separatorPen.Freeze();

        for (int i = 0; i < count; i++)
        {
            try
            {
                DrawSingleWedge(dc, center, animRadius, i, count, hasItems, separatorPen);
            }
            catch (Exception ex)
            {
                // Never let one bad wedge kill the rest of the wheel.
                ShortcutWheel.App.LogError($"Wedge {i} render failed: {ex}");
            }
        }
    }

    private void DrawSingleWedge(DrawingContext dc, Point center, double animRadius,
        int i, int count, bool hasItems, Pen separatorPen)
    {
        double wedgeAngle = 360.0 / count;
        double startAngle = i * wedgeAngle - 90 - wedgeAngle / 2;
        double endAngle = startAngle + wedgeAngle;

        var wedge = WedgeGeometry.CreateWedge(center, CenterRadius, animRadius, startAngle, endAngle);

        // Slots beyond the real item count are empty padding wedges (no hover, no content).
        bool isEmpty = hasItems && i >= _items.Count;
        bool isHovered = !isEmpty && i == _hoveredIndex;

        Brush fill = BuildWedgeFill(center, animRadius, isHovered);
        dc.DrawGeometry(fill, separatorPen, wedge);

        if (isEmpty) return;

        double midAngle = (startAngle + endAngle) / 2;

        // Number indicator (CSGO has 1..6 near the centre)
        double numRadius = CenterRadius + Math.Min(22, Math.Max(0, (animRadius - CenterRadius) * 0.12));
        Point numPos = WedgeGeometry.PolarToCartesian(center, numRadius, midAngle);
        var numBrush = new SolidColorBrush(isHovered
            ? TrimColor
            : Color.FromArgb(0xAA, TrimColor.R, TrimColor.G, TrimColor.B));
        numBrush.Freeze();

        var num = new FormattedText((i + 1).ToString(),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                         FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13, numBrush, 96);
        dc.DrawText(num, new Point(numPos.X - num.Width / 2, numPos.Y - num.Height / 2));

        if (!hasItems)
        {
            DrawPlaceholderLabel(dc, center, animRadius, midAngle);
            return;
        }

        // Real items: icon + label
        var item = _items[i];
        DrawWedgeContent(dc, item, center, animRadius, midAngle, isHovered);
    }

    private Brush BuildWedgeFill(Point center, double animRadius, bool hovered)
    {
        // When a background image is showing, make wedges semi-transparent so
        // the image is visible through them — matching the config window cards.
        byte alpha = HasBackgroundImage ? (byte)0xCC : (byte)0xFF;

        Color inner = hovered ? Lighten(HoverColor, 0.2) : Lighten(AccentColor, 0.18);
        Color outer = hovered ? Darken(HoverColor, 0.25)
                              : Color.FromArgb(0xF0, AccentColor.R, AccentColor.G, AccentColor.B);

        // Apply the background-image alpha override.
        inner = Color.FromArgb(alpha, inner.R, inner.G, inner.B);
        outer = Color.FromArgb((byte)Math.Min(alpha, outer.A), outer.R, outer.G, outer.B);

        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(inner, 0.0));
        brush.GradientStops.Add(new GradientStop(outer, 1.0));
        brush.Freeze();
        return brush;
    }

    private void DrawWedgeContent(DrawingContext dc, ShortcutItem item, Point center,
        double animRadius, double midAngle, bool isHovered)
    {
        double wedgeThickness = Math.Max(0, animRadius - CenterRadius);
        double iconSize = Math.Min(40, wedgeThickness * 0.30);

        // Content anchor: a point in the middle of the wedge, clamped inside
        // the disc so neither icon nor label can escape the rim.
        double contentRadius = CenterRadius + wedgeThickness * 0.52;
        Point anchor = WedgeGeometry.PolarToCartesian(center, contentRadius, midAngle);

        // Icon centred on the anchor, shifted up by half its height + a small gap.
        double iconY = anchor.Y - iconSize / 2 - 2;
        double iconX = anchor.X - iconSize / 2;

        // Clamp the icon rect inside the wheel.
        ClampInsideWheel(center, ref iconX, ref iconY, iconSize, iconSize, WheelRadius - 6);

        if (!string.IsNullOrEmpty(item.TargetPath) && iconSize > 0)
        {
            var icon = IconExtractor.GetCached(item.TargetPath, () =>
                Application.Current?.Dispatcher.Invoke(InvalidateVisual));
            if (icon != null)
                dc.DrawImage(icon, new Rect(iconX, iconY, iconSize, iconSize));
        }

        // Label sits directly below the icon.
        int slotCount = Math.Max(MinWedgeSlots, _items.Count);
        double chordWidth = 2 * contentRadius * Math.Sin(Math.PI / slotCount) - 16;
        int maxChars = Math.Max(4, (int)(chordWidth / 9));
        string label = TruncateText(item.Label, Math.Min(12, maxChars));

        var labelBrush = new SolidColorBrush(isHovered ? TrimColor : TextColor);
        labelBrush.Freeze();

        var formattedText = new FormattedText(label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                         FontStyles.Normal,
                         isHovered ? FontWeights.Bold : FontWeights.SemiBold,
                         FontStretches.Normal),
            16, labelBrush, 96);

        // Place the icon a bit higher so the label sits visibly below it,
        // centred horizontally on the wedge midline.
        // Label sits directly below the icon, horizontally centred on the anchor.
        double drawX = anchor.X - formattedText.Width / 2;
        double drawY = iconY + iconSize + 2;

        // Final safety clamp: keep the entire text rectangle inside the wheel disc.
        ClampInsideWheel(center, ref drawX, ref drawY,
            formattedText.Width, formattedText.Height, WheelRadius - 6);

        // Draw a dark shadow first to ensure readability over any background image.
        var shadowBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
        shadowBrush.Freeze();
        var shadow = new FormattedText(label,
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                         FontStyles.Normal,
                         isHovered ? FontWeights.Bold : FontWeights.SemiBold,
                         FontStretches.Normal),
            16, shadowBrush, 96);
        dc.DrawText(shadow, new Point(drawX + 1, drawY + 1));

        dc.DrawText(formattedText, new Point(drawX, drawY));

        // Sub-menu indicator: small › drawn just to the right of the label,
        // at the same radial position so it stays inside the wedge.
        if (item.IsFolder)
        {
            var arrowBrush = new SolidColorBrush(Color.FromArgb(0xCC, TrimColor.R, TrimColor.G, TrimColor.B));
            arrowBrush.Freeze();
            var arrowText = new FormattedText("›",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, arrowBrush, 96);
            // Place it just to the right of the label text, vertically centred.
            dc.DrawText(arrowText, new Point(
                drawX + formattedText.Width + 2,
                drawY + formattedText.Height / 2 - arrowText.Height / 2));
        }
    }

    private void DrawPlaceholderLabel(DrawingContext dc, Point center, double animRadius, double midAngle)
    {
        double wedgeThickness = Math.Max(0, animRadius - CenterRadius);
        double textRadius = CenterRadius + wedgeThickness * 0.6;
        Point textPos = WedgeGeometry.PolarToCartesian(center, textRadius, midAngle);

        var brush = new SolidColorBrush(Color.FromArgb(0x88, TextColor.R, TextColor.G, TextColor.B));
        brush.Freeze();

        var hint = new FormattedText("拖入此处",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                         FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            13, brush, 96);
        dc.DrawText(hint, new Point(textPos.X - hint.Width / 2, textPos.Y - hint.Height / 2));
    }

    private void DrawOuterRing(DrawingContext dc, Point center, double r)
    {
        var trim = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, TrimColor.R, TrimColor.G, TrimColor.B)), 2.5);
        trim.Freeze();
        dc.DrawEllipse(null, trim, center, r - 1, r - 1);

        var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x40, TrimColor.R, TrimColor.G, TrimColor.B)), 6);
        glow.Freeze();
        dc.DrawEllipse(null, glow, center, r + 2, r + 2);
    }

    private void DrawCenterCircle(DrawingContext dc, Point center, bool hasItems)
    {
        // Subtle vertical gradient on the centre disc – CSGO logo background.
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(
            HasBackgroundImage ? (byte)0xCC : (byte)0xFF, 0x2A, 0x24, 0x1B), 0.0));
        grad.GradientStops.Add(new GradientStop(Color.FromArgb(
            HasBackgroundImage ? (byte)0xCC : (byte)0xFF, 0x10, 0x0E, 0x0C), 1.0));
        grad.Freeze();

        var ringPen = new Pen(new SolidColorBrush(TrimColor), 2);
        ringPen.Freeze();
        dc.DrawEllipse(grad, ringPen, center, CenterRadius - 2, CenterRadius - 2);

        // Centre label: CS-style two-line emblem when at root, ‹ when in submenu.
        if (IsSubMenu)
        {
            var brush = new SolidColorBrush(TrimColor);
            brush.Freeze();
            var back = new FormattedText("‹",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                             FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                CenterRadius * 0.8, brush, 96);
            dc.DrawText(back, new Point(center.X - back.Width / 2, center.Y - back.Height / 2 - 2));
        }
        else
        {
            DrawCenterLogo(dc, center);
        }

        // Optional page indicator just below the centre logo.
        if (!string.IsNullOrEmpty(PageInfo))
        {
            var pageBrush = new SolidColorBrush(Color.FromArgb(0xCC, TrimColor.R, TrimColor.G, TrimColor.B));
            pageBrush.Freeze();
            var pageText = new FormattedText(PageInfo,
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                             FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                10, pageBrush, 96);
            double y = center.Y + CenterRadius - 16;
            dc.DrawText(pageText, new Point(center.X - pageText.Width / 2, y));
        }
    }

    private void DrawCenterLogo(DrawingContext dc, Point center)
    {
        var brush = new SolidColorBrush(TrimColor);
        brush.Freeze();

        // Star glyph as a stand-in for a logo
        var star = new FormattedText("★",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Symbol"), CenterRadius * 0.55, brush, 96);
        dc.DrawText(star, new Point(center.X - star.Width / 2, center.Y - star.Height / 2 - 4));

        // Tiny label under the star
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xCC, TrimColor.R, TrimColor.G, TrimColor.B));
        labelBrush.Freeze();
        var label = new FormattedText("ESC",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"),
                         FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10, labelBrush, 96);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + CenterRadius * 0.30));
    }

    private void DrawEmptyHint(DrawingContext dc, Point center, double animRadius)
    {
        var titleBrush = new SolidColorBrush(Color.FromArgb(0xDD, TrimColor.R, TrimColor.G, TrimColor.B));
        titleBrush.Freeze();
        var title = new FormattedText("将快捷方式拖入扇区",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
                         FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            13, titleBrush, 96);

        double y = center.Y + CenterRadius + Math.Max(0, animRadius - CenterRadius) * 0.10;
        dc.DrawText(title, new Point(center.X - title.Width / 2, y));
    }

    /// <summary>
    /// Pull the (drawX, drawY)-anchored rectangle back along the radial
    /// direction until all four corners lie within <paramref name="maxRadius"/>.
    /// This is the last line of defence against label text spilling outside
    /// the wheel disc.
    /// </summary>
    private static void ClampInsideWheel(Point center, ref double drawX, ref double drawY,
        double width, double height, double maxRadius)
    {
        // All four corners of the text rect must be inside the disc.
        double FurthestDistance(double x, double y)
        {
            double[] xs = { x, x + width };
            double[] ys = { y, y + height };
            double max = 0;
            foreach (var px in xs)
            {
                foreach (var py in ys)
                {
                    double d = Math.Sqrt((px - center.X) * (px - center.X) +
                                         (py - center.Y) * (py - center.Y));
                    if (d > max) max = d;
                }
            }
            return max;
        }

        // Iteratively pull the rectangle toward the centre when any corner
        // is too far out. Up to 8 passes is plenty given the small steps.
        for (int pass = 0; pass < 8; pass++)
        {
            double dist = FurthestDistance(drawX, drawY);
            if (dist <= maxRadius) return;

            // Direction from worst-case corner toward centre — approximated
            // by direction from rect centre toward wheel centre.
            double cx = drawX + width / 2;
            double cy = drawY + height / 2;
            double dx = center.X - cx;
            double dy = center.Y - cy;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5) return;

            double pull = dist - maxRadius + 1;
            drawX += dx / len * pull;
            drawY += dy / len * pull;
        }
    }

    private static Color Lighten(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(c.A,
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));
    }

    private static Color Darken(Color c, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(c.A,
            (byte)(c.R * (1 - amount)),
            (byte)(c.G * (1 - amount)),
            (byte)(c.B * (1 - amount)));
    }

    private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3);

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? "";
        return text[..(maxLength - 1)] + "…";
    }

    #region Hit Testing

    public int HitTestWedge(Point mousePos)
    {
        Point center = new Point(WheelRadius, WheelRadius);
        bool hasItems = _items.Count > 0;
        int slotCount = hasItems ? Math.Max(MinWedgeSlots, _items.Count) : PlaceholderSlots;
        var (index, _) = WedgeGeometry.HitTest(center, CenterRadius, WheelRadius, mousePos, slotCount);
        // Slots beyond real items are empty padding — treat as a miss.
        if (hasItems && index >= _items.Count) return -1;
        return index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        int idx = HitTestWedge(pos);
        if (idx != _hoveredIndex)
        {
            _hoveredIndex = idx;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        int idx = HitTestWedge(pos);

        if (idx == -2)
        {
            CenterClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (idx >= 0 && idx < _items.Count)
        {
            WedgeClicked?.Invoke(this, idx);
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        // Right-click navigation is handled at the window level
        // (OverlayWindow.Window_MouseDown) so that misses outside the
        // wheel still navigate back. We only mark the event as handled
        // here to keep downstream handlers from interfering.
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoveredIndex = -1;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double size = WheelRadius * 2;
        return new Size(size, size);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double size = WheelRadius * 2;
        return new Size(size, size);
    }

    #endregion
}

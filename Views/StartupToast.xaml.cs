using System.Windows;
using System.Windows.Media.Animation;

namespace ShortcutWheel.Views;

public partial class StartupToast : Window
{
    public StartupToast(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;

        // Start fully off-screen so the first WPF composition pass — which
        // is the one that looks "gray/blurry" on transparent windows — is
        // invisible to the user. Once layout has run we move the window to
        // its final position at full opacity.
        Left = -32000;
        Top = -32000;
        Opacity = 1.0;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Defer to the next dispatcher pass so ActualWidth/ActualHeight are
        // settled (SizeToContent has finished) and WPF has rendered at least
        // one frame in the off-screen position.
        Dispatcher.BeginInvoke(new Action(MoveOnScreenAndAnimate),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MoveOnScreenAndAnimate()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0) dpi = 1.0;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            // Fallback: force a measure pass.
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new Rect(0, 0, DesiredSize.Width, DesiredSize.Height));
            w = DesiredSize.Width;
            h = DesiredSize.Height;
        }

        double targetLeft = (work.Right / dpi) - w - 24;
        double targetTop = (work.Bottom / dpi) - h - 24;

        Left = targetLeft;
        Top = targetTop;

        // Slide in from the right (translate from +60px to 0). Using a render
        // transform avoids opacity blending — the toast is fully visible from
        // frame one, which is what eliminates the "gray flash".
        var slideIn = new DoubleAnimation
        {
            From = 60.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideIn);

        // After 4.5 seconds: slide back out to the right and close.
        var hideTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(4500)
        };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();

            var slideOut = new DoubleAnimation
            {
                From = 0.0,
                To = 80.0,
                Duration = TimeSpan.FromMilliseconds(320),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var fadeOut = new DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                BeginTime = TimeSpan.FromMilliseconds(120),
                Duration = TimeSpan.FromMilliseconds(220)
            };
            fadeOut.Completed += (_, _) => Close();

            SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideOut);
            BeginAnimation(OpacityProperty, fadeOut);
        };
        hideTimer.Start();
    }

    /// <summary>
    /// Fire-and-forget helper: pops a toast in the bottom-right corner.
    /// </summary>
    public static void Show(string title, string body)
    {
        try
        {
            var toast = new StartupToast(title, body);
            toast.Show();
        }
        catch
        {
            // Non-fatal — failing to show a toast must never crash the host.
        }
    }
}

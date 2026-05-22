using System.Diagnostics;
using System.Windows;
using ShortcutWheel.Services;

namespace ShortcutWheel.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _service;
    private readonly UpdateInfo _info;

    public UpdateWindow(UpdateService service, UpdateInfo info)
    {
        InitializeComponent();
        _service = service;
        _info = info;

        CurrentVersionText.Text = "v" + UpdateService.GetCurrentVersion();
        NewVersionText.Text = string.IsNullOrEmpty(info.TagName) ? ("v" + info.Version) : info.TagName;

        NotesText.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes)
            ? "(no release notes)"
            : info.ReleaseNotes.Trim();

        StatusText.Text = $"大小 / Size: {FormatBytes(info.AssetSize)}";
    }

    private void BtnLater_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_info.ReleaseUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _info.ReleaseUrl,
                UseShellExecute = true
            });
        }
        catch (System.Exception ex)
        {
            App.LogError($"Open release url failed: {ex.Message}");
        }
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnLater.IsEnabled = false;
        BtnViewOnGitHub.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "下载中... / Downloading...";

        try
        {
            var progress = new System.Progress<double>(p =>
            {
                DownloadProgress.Value = p * 100;
                StatusText.Text = $"下载中 / Downloading... {p:P0}";
            });

            string temp = await _service.DownloadUpdateAsync(_info, progress);

            StatusText.Text = "下载完成，正在应用更新... / Applying...";
            await System.Threading.Tasks.Task.Delay(500);

            // ApplyUpdateAndRestart calls Application.Shutdown() internally
            // after spawning the helper batch.
            _service.ApplyUpdateAndRestart(temp);
        }
        catch (System.Exception ex)
        {
            StatusText.Text = $"下载失败 / Failed: {ex.Message}";
            App.LogError($"Update download failed: {ex}");
            BtnInstall.IsEnabled = true;
            BtnLater.IsEnabled = true;
            BtnViewOnGitHub.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return $"{v:F1} {units[u]}";
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;

namespace LinkTwo.UI;

/// <summary>
/// What the tray icon opens: status, Now Playing and quick actions, placed above the tray like Windows 11's
/// own flyouts. Slides up + fades in; fades + drops out when it loses focus.
/// </summary>
public partial class TrayFlyout
{
    private readonly AppModel _model;
    private bool _hiding;
    private DateTime _hiddenAt = DateTime.MinValue;

    public TrayFlyout(AppModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        Deactivated += (_, _) => HideAnimated();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HideAnimated(); };
        model.PropertyChanged += OnModelChanged;
        ApplyReconnecting();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppModel.Reconnecting)) ApplyReconnecting();
    }

    private void ApplyReconnecting()
    {
        var busy = _model.Reconnecting;
        ReconnectIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ReconnectSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ReconnectText.Text = busy ? "Reconnecting…" : "Reconnect";
    }

    public void Toggle()
    {
        // Clicking the tray icon while open first deactivates (hides) us; don't immediately reopen.
        if (IsVisible && !_hiding) HideAnimated();
        else if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds > 250) ShowAnimated();
    }

    private void ShowAnimated()
    {
        _hiding = false;
        // The window keeps its native Acrylic backdrop (so it can't be translucent itself); its content animates.
        Root.Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        Activate();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    public void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _hiddenAt = DateTime.UtcNow;
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (!_hiding) return; // reopened meanwhile
            Hide();
            _hiding = false;
        };
        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void Open_Click(object sender, RoutedEventArgs e) => AppController.Shared.ShowDashboard();
    private void SendFiles_Click(object sender, RoutedEventArgs e) => AppController.Shared.ChooseFiles();
    private void Clipboard_Click(object sender, RoutedEventArgs e) => AppController.Shared.SendClipboard();
    private void Pause_Click(object sender, RoutedEventArgs e) => AppController.Shared.SetPaused(!_model.Paused);
    private void Reconnect_Click(object sender, RoutedEventArgs e) => AppController.Shared.Reconnect();
    private void Quit_Click(object sender, RoutedEventArgs e) => AppController.Shared.Quit();
}

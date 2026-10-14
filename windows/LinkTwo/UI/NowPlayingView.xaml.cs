using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace LinkTwo.UI;

/// <summary>Now Playing with full control: transport, live progress with scrubbing, and phone volume.</summary>
public partial class NowPlayingView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NowPlayingState? _state;
    private bool _dragging;
    private Storyboard? _bars;

    public NowPlayingView()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => UpdateProgress();
        DataContextChanged += (_, e) => Attach(e.NewValue as NowPlayingState);
        Loaded += (_, _) => { _tick.Start(); Attach(DataContext as NowPlayingState); };
        Unloaded += (_, _) => _tick.Stop();
    }

    private void Attach(NowPlayingState? state)
    {
        if (_state != null) _state.PropertyChanged -= OnStateChanged;
        _state = state;
        if (_state == null) return;
        _state.PropertyChanged += OnStateChanged;
        ApplyPlaying(animate: false);
        ApplyLayout();
        UpdateProgress();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingState.Playing): ApplyPlaying(animate: true); break;
            case nameof(NowPlayingState.Duration) or nameof(NowPlayingState.MaxVolume) or nameof(NowPlayingState.Volume)
                or nameof(NowPlayingState.CanSeek): ApplyLayout(); break;
            case nameof(NowPlayingState.Position): UpdateProgress(); break;
        }
    }

    private void ApplyLayout()
    {
        if (_state == null) return;
        ProgressRow.Visibility = _state.HasDuration ? Visibility.Visible : Visibility.Collapsed;
        Progress.Maximum = Math.Max(_state.Duration, 1);
        Progress.IsEnabled = _state.CanSeek;
        VolumeRow.Visibility = _state.HasVolume ? Visibility.Visible : Visibility.Collapsed;
        Volume.Maximum = Math.Max(_state.MaxVolume, 1);
        if (!Volume.IsMouseCaptureWithin) Volume.Value = _state.Volume;
    }

    /// <summary>Artwork settles back slightly while paused (like Media Player); the bars rest.</summary>
    private void ApplyPlaying(bool animate)
    {
        if (_state == null) return;
        var playing = _state.Playing;
        PlayPauseIcon.Symbol = playing ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        PlayPause.ToolTip = playing ? "Pause" : "Play";

        var scale = playing ? 1.0 : 0.9;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        var dur = animate ? TimeSpan.FromMilliseconds(380) : TimeSpan.Zero;
        ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
        ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
        ArtShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(playing ? 0.35 : 0.12, dur));

        if (playing) StartBars();
        else StopBars();
    }

    private void StartBars()
    {
        if (_bars != null) return;
        _bars = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Bounce(FrameworkElement bar, double from, double to, double seconds)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(a, bar);
            Storyboard.SetTargetProperty(a, new PropertyPath(HeightProperty));
            _bars.Children.Add(a);
        }
        Bounce(Bar1, 3, 10, 0.42);
        Bounce(Bar2, 9, 3, 0.36);
        Bounce(Bar3, 4, 9, 0.5);
        _bars.Begin(this, true);
    }

    private void StopBars()
    {
        _bars?.Stop(this);
        _bars = null;
        Bar1.Height = 3;
        Bar2.Height = 3;
        Bar3.Height = 3;
    }

    private void UpdateProgress()
    {
        if (_state == null || !_state.HasDuration) return;
        var current = _dragging ? Progress.Value : _state.Elapsed(DateTime.UtcNow);
        if (!_dragging) Progress.Value = current;
        Elapsed.Text = Format(current);
        Remaining.Text = "-" + Format(Math.Max(0, _state.Duration - current));
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Progress_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Progress_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        AppController.Shared.MediaControl("seek", Progress.Value);
    }

    /// <summary>A click on the track (no drag) also seeks.</summary>
    private void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _state is not { CanSeek: true }) return;
        AppController.Shared.MediaControl("seek", Progress.Value);
    }

    private void Volume_DragCompleted(object sender, DragCompletedEventArgs e) =>
        AppController.Shared.MediaControl("volume", Volume.Value);

    private void Volume_MouseUp(object sender, MouseButtonEventArgs e) =>
        AppController.Shared.MediaControl("volume", Volume.Value);

    private void Previous_Click(object sender, RoutedEventArgs e) => AppController.Shared.MediaControl("previous");
    private void PlayPause_Click(object sender, RoutedEventArgs e) => AppController.Shared.MediaControl("playpause");
    private void Next_Click(object sender, RoutedEventArgs e) => AppController.Shared.MediaControl("next");
}

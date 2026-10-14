using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace LinkTwo.Core;

/// <summary>
/// Puts the phone's Now Playing into Windows' own media controls (the media card in Quick Settings and the
/// volume flyout, plus keyboard media keys), via SystemMediaTransportControls. Button presses go back to the phone.
/// </summary>
public sealed class MediaBridge
{
    private MediaPlayer? _player;
    private SystemMediaTransportControls? _smtc;
    private string? _artKey;

    /// <summary>"play" | "pause" | "next" | "previous"</summary>
    public event Action<string>? Command;
    /// <summary>Seek requested from Windows' media card (seconds).</summary>
    public event Action<double>? Seek;

    private SystemMediaTransportControls Controls()
    {
        if (_smtc != null) return _smtc;
        // A MediaPlayer that never plays: we only borrow its transport controls session.
        _player = new MediaPlayer();
        _player.CommandManager.IsEnabled = false;
        var smtc = _player.SystemMediaTransportControls;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.ButtonPressed += (_, e) =>
        {
            var cmd = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => "play",
                SystemMediaTransportControlsButton.Pause => "pause",
                SystemMediaTransportControlsButton.Next => "next",
                SystemMediaTransportControlsButton.Previous => "previous",
                _ => null,
            };
            if (cmd != null) Command?.Invoke(cmd);
        };
        smtc.PlaybackPositionChangeRequested += (_, e) => Seek?.Invoke(e.RequestedPlaybackPosition.TotalSeconds);
        _smtc = smtc;
        return smtc;
    }

    public async void Update(string key, string title, string artist, bool playing, byte[]? art,
                             double duration, double position, bool canSeek)
    {
        try
        {
            var smtc = Controls();
            smtc.IsEnabled = true;
            var u = smtc.DisplayUpdater;
            u.Type = MediaPlaybackType.Music;
            u.MusicProperties.Title = title;
            u.MusicProperties.Artist = artist;
            if (art != null && art.Length > 0)
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(art);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                u.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
                _artKey = key;
            }
            else if (_artKey != key)
            {
                u.Thumbnail = null;
            }
            u.Update();

            smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (duration > 0)
            {
                smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    MaxSeekTime = canSeek ? TimeSpan.FromSeconds(duration) : TimeSpan.Zero,
                    EndTime = TimeSpan.FromSeconds(duration),
                    Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
                });
            }
        }
        catch (Exception e)
        {
            Log.Error("media controls", e);
        }
    }

    public void Clear()
    {
        try
        {
            if (_smtc == null) return;
            _smtc.DisplayUpdater.ClearAll();
            _smtc.IsEnabled = false;
            _artKey = null;
        }
        catch (Exception e)
        {
            Log.Error("media controls clear", e);
        }
    }
}

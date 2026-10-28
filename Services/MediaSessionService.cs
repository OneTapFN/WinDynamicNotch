using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinDynamicNotch.Services;

public sealed record MediaSnapshot(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    string SourceApp,
    bool IsPlaying,
    bool CanSeek,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Artwork);

/// <summary>
/// Обёртка над Windows SMTC (GlobalSystemMediaTransportControlsSessionManager):
/// читает текущий трек, обложку, таймлайн и управляет воспроизведением.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private const long MaxThumbnailBytes = 24 * 1024 * 1024;

    /// <summary>
    /// Через сколько после смены трека перепроверять обложку. SMTC часто отдаёт
    /// новую мету раньше, чем новую картинку, поэтому одного события недостаточно.
    /// </summary>
    private static readonly TimeSpan[] ArtworkCheckDelays =
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(2500),
        TimeSpan.FromMilliseconds(4000),
        TimeSpan.FromMilliseconds(6000)
    };

    private readonly Dispatcher _dispatcher;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineHandler;

    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _currentSessionHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsHandler;

    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _sourceApp = string.Empty;

    private bool _isPlaying;
    private bool _canSeek;
    private TimeSpan _timelineStart;
    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private DateTime _timelineStampUtc;

    private ImageSource? _artwork;
    private string _artworkKey = string.Empty;
    private ulong _artworkHash;

    private bool _refreshing;
    private bool _pendingRefresh;
    private bool _forceArtworkCheck;
    private bool _disposed;
    private int _artworkCheckGeneration;

    public MediaSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public event EventHandler? Changed;

    public MediaSnapshot Current => new(
        _session is not null,
        _title,
        _artist,
        _album,
        _sourceApp,
        _isPlaying,
        _canSeek,
        GetPosition(),
        _duration,
        _artwork);

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _currentSessionHandler = (_, _) => PostRefreshWithChecks();
            _sessionsHandler = (_, _) => PostRefreshWithChecks();
            _manager.CurrentSessionChanged += _currentSessionHandler;
            _manager.SessionsChanged += _sessionsHandler;
        }
        catch (Exception)
        {
            _manager = null;
        }

        await RefreshAsync();
    }

    public TimeSpan GetPosition()
    {
        var position = _basePosition;
        if (_isPlaying)
        {
            position += DateTime.UtcNow - _timelineStampUtc;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && position > _duration)
        {
            position = _duration;
        }

        return position;
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }
        }
        catch (Exception)
        {
            // ignored
        }

        PostRefresh();
    }

    public async Task SkipNextAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
        }
        catch (Exception)
        {
            // ignored
        }

        PostRefresh();
    }

    public async Task SkipPreviousAsync()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
        }
        catch (Exception)
        {
            // ignored
        }

        PostRefresh();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (_duration > TimeSpan.Zero && position > _duration)
        {
            position = _duration;
        }

        try
        {
            var target = _timelineStart + position;
            if (await session.TryChangePlaybackPositionAsync(target.Ticks))
            {
                _basePosition = position;
                _timelineStampUtc = DateTime.UtcNow;
                RaiseChanged();
            }
        }
        catch (Exception)
        {
            // ignored
        }

        PostRefresh();
    }

    private void PostRefresh()
    {
        _dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    /// <summary>Обновление + серия отложенных проверок обложки.</summary>
    private void PostRefreshWithChecks()
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            ScheduleArtworkChecks();
            _ = RefreshAsync();
        }));
    }

    private void ScheduleArtworkChecks()
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_artworkCheckGeneration;
        foreach (var delay in ArtworkCheckDelays)
        {
            _ = RunArtworkCheckAsync(delay, generation);
        }
    }

    private async Task RunArtworkCheckAsync(TimeSpan delay, int generation)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || generation != _artworkCheckGeneration)
            {
                return;
            }

            _forceArtworkCheck = true;
            _ = RefreshAsync();
        }));
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _pendingRefresh = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _pendingRefresh = false;
                await RefreshCoreAsync();
            }
            while (_pendingRefresh);
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var session = SelectSession();
        if (!ReferenceEquals(session, _session))
        {
            AttachSession(session);
            _session = session;
        }

        if (session is null)
        {
            ResetState();
            RaiseChanged();
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            _isPlaying = playback?.PlaybackStatus ==
                         GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var timeline = session.GetTimelineProperties();
            if (timeline is not null)
            {
                _timelineStart = timeline.StartTime;
                _duration = timeline.EndTime > timeline.StartTime
                    ? timeline.EndTime - timeline.StartTime
                    : TimeSpan.Zero;
                _basePosition = timeline.Position;
                _timelineStampUtc = DateTime.UtcNow;
                _canSeek = _duration > TimeSpan.Zero;
            }
            else
            {
                _canSeek = false;
            }

            _sourceApp = FriendlyAppName(session.SourceAppUserModelId);

            var forceArtwork = _forceArtworkCheck;
            _forceArtworkCheck = false;

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is not null)
            {
                _title = properties.Title ?? string.Empty;
                _artist = properties.Artist ?? string.Empty;
                _album = properties.AlbumTitle ?? string.Empty;

                var key = string.Join(
                    '\u0001',
                    properties.Title,
                    properties.Artist,
                    properties.AlbumTitle,
                    _sourceApp);

                var keyChanged = !string.Equals(key, _artworkKey, StringComparison.Ordinal);
                if (keyChanged)
                {
                    _artworkKey = key;
                }

                if (keyChanged || forceArtwork || (_artwork is null && properties.Thumbnail is not null))
                {
                    var (image, hash) = await LoadArtworkAsync(properties.Thumbnail);

                    if (image is null)
                    {
                        // Обложки нет вовсе — не держим картинку от прошлого трека.
                        if (keyChanged)
                        {
                            _artwork = null;
                            _artworkHash = 0;
                        }
                    }
                    else if (hash != _artworkHash || _artwork is null)
                    {
                        _artwork = image;
                        _artworkHash = hash;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Сессия могла закрыться прямо во время чтения — оставляем последние данные.
        }

        RaiseChanged();
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession()
    {
        if (_manager is null)
        {
            return null;
        }

        try
        {
            var sessions = _manager.GetSessions();
            if (sessions is null || sessions.Count == 0)
            {
                return null;
            }

            var current = _manager.GetCurrentSession();
            if (current is not null && IsPlaying(current))
            {
                return current;
            }

            var playing = sessions.FirstOrDefault(IsPlaying);
            return playing ?? current ?? sessions[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        DetachSessionHandlers();

        if (session is null)
        {
            return;
        }

        _mediaHandler = (_, _) => PostRefreshWithChecks();
        _playbackHandler = (_, _) => PostRefresh();
        _timelineHandler = (_, _) => PostRefresh();

        try
        {
            session.MediaPropertiesChanged += _mediaHandler;
            session.PlaybackInfoChanged += _playbackHandler;
            session.TimelinePropertiesChanged += _timelineHandler;
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private void DetachSessionHandlers()
    {
        var session = _session;
        if (session is null)
        {
            _mediaHandler = null;
            _playbackHandler = null;
            _timelineHandler = null;
            return;
        }

        try
        {
            if (_mediaHandler is not null)
            {
                session.MediaPropertiesChanged -= _mediaHandler;
            }

            if (_playbackHandler is not null)
            {
                session.PlaybackInfoChanged -= _playbackHandler;
            }

            if (_timelineHandler is not null)
            {
                session.TimelinePropertiesChanged -= _timelineHandler;
            }
        }
        catch (Exception)
        {
            // ignored
        }

        _mediaHandler = null;
        _playbackHandler = null;
        _timelineHandler = null;
    }

    private void ResetState()
    {
        _title = string.Empty;
        _artist = string.Empty;
        _album = string.Empty;
        _sourceApp = string.Empty;
        _isPlaying = false;
        _canSeek = false;
        _timelineStart = TimeSpan.Zero;
        _duration = TimeSpan.Zero;
        _basePosition = TimeSpan.Zero;
        _timelineStampUtc = DateTime.UtcNow;
        _artwork = null;
        _artworkKey = string.Empty;
        _artworkHash = 0;
        _forceArtworkCheck = false;
        _artworkCheckGeneration++;
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<(ImageSource? Image, ulong Hash)> LoadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return (null, 0);
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > MaxThumbnailBytes)
            {
                return (null, 0);
            }

            var buffer = new byte[(int)stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(buffer);
            }

            var hash = ComputeHash(buffer);

            using var memory = new MemoryStream(buffer);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.DecodePixelWidth = 512;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return (bitmap, hash);
        }
        catch (Exception)
        {
            return (null, 0);
        }
    }

    /// <summary>FNV-1a: нужен только чтобы понять, изменилась ли картинка.</summary>
    private static ulong ComputeHash(byte[] data)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in data)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static string FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return string.Empty;
        }

        var id = appUserModelId;
        var separator = id.IndexOf('!');
        if (separator >= 0 && separator + 1 < id.Length)
        {
            id = id[(separator + 1)..];
        }

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            id = id[..^4];
        }

        return id switch
        {
            "Microsoft.ZuneMusic" => "Media Player",
            "Microsoft.ZuneVideo" => "Movies & TV",
            "Spotify" => "Spotify",
            "Music.UI" => "Media Player",
            _ => id
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _artworkCheckGeneration++;

        try
        {
            if (_manager is not null)
            {
                if (_currentSessionHandler is not null)
                {
                    _manager.CurrentSessionChanged -= _currentSessionHandler;
                }

                if (_sessionsHandler is not null)
                {
                    _manager.SessionsChanged -= _sessionsHandler;
                }
            }
        }
        catch (Exception)
        {
            // ignored
        }

        _currentSessionHandler = null;
        _sessionsHandler = null;

        DetachSessionHandlers();
        _session = null;
        _manager = null;
    }
}

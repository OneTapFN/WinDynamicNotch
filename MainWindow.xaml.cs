using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinDynamicNotch.Interop;
using WinDynamicNotch.Services;

namespace WinDynamicNotch;

public partial class MainWindow : Window
{
    private const double CompactWindowHeight = 70;
    private const double ExpandedWindowHeight = 558;

    /// <summary>Диаметр кружка-«камеры», из которого выезжает панель.</summary>
    private const double CameraCircleSize = 34;

    private const double CardCornerRadius = 22;
    private const double ShadowCornerRadius = 24;

    /// <summary>Тень на 2 DIP больше карточки с каждой стороны (margin 10 против 12).</summary>
    private const double ShadowOverhang = 4;

    // Зона появления: полоса сверху по центру экрана (значения в DIP).
    private const double HotZoneHalfWidth = 180;
    private const double HotZoneHeight = 26;

    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    /// <summary>Обычный и «подсвеченный» цвет ободка карточки (кружок-камера ярче).</summary>
    private static readonly Color IdleBorderColor = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Color ActiveBorderColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

    /// <summary>Сколько панель висит после того, как курсор ушёл с неё.</summary>
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(900);

    /// <summary>Сколько нужно задержать курсор в верхней зоне, чтобы панель появилась.</summary>
    private static readonly TimeSpan HoverRevealDelay = TimeSpan.FromMilliseconds(420);

    /// <summary>Сколько панель остаётся на экране после нажатия медиаклавиши.</summary>
    private static readonly TimeSpan MediaKeyVisibleTime = TimeSpan.FromSeconds(3);

    private readonly MediaSessionService _media;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly KeyboardHook _keyboardHook;

    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly TranslateTransform _contentTranslate = new();
    private readonly SolidColorBrush _cardBorderBrush = new(IdleBorderColor);
    private readonly ScaleTransform _artScale = new(1, 1);
    private readonly TranslateTransform _artTranslate = new();
    private readonly TranslateTransform _titleTranslate = new();
    private readonly TranslateTransform _seekTranslate = new();
    private readonly TranslateTransform _controlsTranslate = new();

    private bool _isExpanded;
    private bool _isSeeking;
    private bool _isPanelVisible;
    private bool _initialized;
    private double _durationSeconds;
    private double _dpiScale = 1;
    private DateTime _lastInteractionUtc = DateTime.UtcNow;
    private DateTime? _outsideSinceUtc;
    private DateTime? _hotZoneSinceUtc;
    private DateTime _keepVisibleUntilUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        SetUpTransforms();
        RepositionWindow();

        _media = new MediaSessionService(Dispatcher);
        _media.Changed += (_, _) => RefreshUi();

        _keyboardHook = new KeyboardHook();
        _keyboardHook.MediaKeyPressed += (_, _) => OnMediaKeyPressed();

        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _hoverTimer.Tick += (_, _) => OnHoverTick();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        _autoCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _autoCollapseTimer.Tick += (_, _) =>
        {
            if (!_isExpanded)
            {
                _autoCollapseTimer.Stop();
                return;
            }

            if (IsMouseOver || _isSeeking)
            {
                _lastInteractionUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - _lastInteractionUtc >= TimeSpan.FromSeconds(8))
            {
                Collapse();
            }
        };

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            _progressTimer.Stop();
            _autoCollapseTimer.Stop();
            _hoverTimer.Stop();
            _keyboardHook.Dispose();
            _media.Dispose();
        };
        PreviewMouseMove += (_, _) => RestartAutoCollapse();
        IsVisibleChanged += (_, _) =>
        {
            if (!_initialized)
            {
                return;
            }

            _isPanelVisible = false;
            _outsideSinceUtc = null;
            _hotZoneSinceUtc = null;
            _keepVisibleUntilUtc = DateTime.MinValue;

            if (IsVisible)
            {
                _hoverTimer.Start();
                ShowPanel();
            }
            else
            {
                _hoverTimer.Stop();
            }
        };
        DpiChanged += (_, e) =>
        {
            _dpiScale = e.NewDpi.DpiScaleX;
            RepositionWindow();
        };

        SeekSlider.AddHandler(
            PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonDown),
            true);
        SeekSlider.AddHandler(
            PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(SeekSlider_PreviewMouseLeftButtonUp),
            true);
        SeekSlider.AddHandler(
            LostMouseCaptureEvent,
            new MouseEventHandler(SeekSlider_LostMouseCapture),
            true);
    }

    private void SetUpTransforms()
    {
        var rootGroup = new TransformGroup();
        rootGroup.Children.Add(_rootScale);
        RootGrid.RenderTransform = rootGroup;
        RootGrid.RenderTransformOrigin = new Point(0.5, 0);

        RootGrid.Opacity = 0;
        RootGrid.IsHitTestVisible = false;

        CardContent.RenderTransform = _contentTranslate;
        CardContent.RenderTransformOrigin = new Point(0.5, 0.5);

        // Свой (не «замороженный») браш, чтобы можно было анимировать цвет ободка.
        Card.BorderBrush = _cardBorderBrush;

        var artGroup = new TransformGroup();
        artGroup.Children.Add(_artScale);
        artGroup.Children.Add(_artTranslate);
        ExpandedArtHost.RenderTransform = artGroup;
        ExpandedArtHost.RenderTransformOrigin = new Point(0.5, 0.5);

        ExpandedTextBlock.RenderTransform = _titleTranslate;
        ExpandedTextBlock.RenderTransformOrigin = new Point(0.5, 0);
        ExpandedSeekBlock.RenderTransform = _seekTranslate;
        ExpandedSeekBlock.RenderTransformOrigin = new Point(0.5, 0);
        ExpandedControls.RenderTransform = _controlsTranslate;
        ExpandedControls.RenderTransformOrigin = new Point(0.5, 0);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        style |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, style);

        // Пока панель скрыта, окно не должно перехватывать мышь.
        SetClickThrough(true);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _initialized = true;

        _progressTimer.Start();
        _hoverTimer.Start();
        _keyboardHook.Install();
        RefreshUi();
        await _media.InitializeAsync();
    }

    // ===== появление / скрытие =====

    private void OnHoverTick()
    {
        if (!_initialized)
        {
            return;
        }

        var inHotZone = IsCursorInHotZone();
        var overPanel = IsCursorOverPanel();

        if (!_isPanelVisible)
        {
            // Пока кнопка мыши зажата (например, тащим окно), панель не выскакивает.
            if (!inHotZone || IsLeftMouseButtonDown())
            {
                _hotZoneSinceUtc = null;
                return;
            }

            _hotZoneSinceUtc ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _hotZoneSinceUtc.Value >= HoverRevealDelay)
            {
                ShowPanel();
            }

            return;
        }

        _hotZoneSinceUtc = null;

        // Курсор на панели или в верхней зоне — панель остаётся и не сворачивается.
        if (overPanel || inHotZone)
        {
            _outsideSinceUtc = null;
            _lastInteractionUtc = DateTime.UtcNow;
            return;
        }

        // Раскрытую карточку прячет таймер автосворачивания.
        if (_isExpanded)
        {
            _outsideSinceUtc = null;
            return;
        }

        var now = DateTime.UtcNow;

        // Панель, показанная медиаклавишей, висит дольше.
        if (now < _keepVisibleUntilUtc)
        {
            _outsideSinceUtc = null;
            return;
        }

        if (_outsideSinceUtc is null)
        {
            _outsideSinceUtc = now;
            return;
        }

        if (now - _outsideSinceUtc.Value >= HideDelay)
        {
            HidePanel();
        }
    }

    private void OnMediaKeyPressed()
    {
        if (!_initialized)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnMediaKeyPressed));
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        ShowPanel();
        _keepVisibleUntilUtc = DateTime.UtcNow + MediaKeyVisibleTime;
        _outsideSinceUtc = null;
        _hotZoneSinceUtc = null;
    }

    private static bool IsLeftMouseButtonDown()
    {
        return (NativeMethods.GetAsyncKeyState(NativeMethods.VkLeftButton) & 0x8000) != 0;
    }

    private void ShowPanel()
    {
        if (_isPanelVisible)
        {
            return;
        }

        _isPanelVisible = true;
        _outsideSinceUtc = null;
        _hotZoneSinceUtc = null;
        _keepVisibleUntilUtc = DateTime.MinValue;
        _lastInteractionUtc = DateTime.UtcNow;

        RootGrid.IsHitTestVisible = true;
        SetClickThrough(false);

        // Окно всегда свёрнуто по высоте, даже если предыдущее сворачивание не успело доехать.
        BeginAnimation(HeightProperty, null);
        Height = CompactWindowHeight;

        var cardWidth = CardSlotWidth();
        var cardHeight = CardSlotHeight();

        // Стартовое состояние: маленький кружок по центру сверху — «камера».
        ResetCardShape();
        Card.Width = CameraCircleSize;
        Card.Height = CameraCircleSize;

        CardShadow.Width = CameraCircleSize + ShadowOverhang;
        CardShadow.Height = CameraCircleSize + ShadowOverhang;

        CardContent.Opacity = 0;
        _contentTranslate.Y = 8;
        _rootScale.ScaleX = 1;
        _rootScale.ScaleY = 1;

        RootGrid.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // Кружок «включается» — короткий подскок.
        var pop = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // Кружок растягивается в панель.
        var growEase = new SineEase { EasingMode = EasingMode.EaseOut };
        var growBegin = TimeSpan.FromMilliseconds(60);
        var growDuration = TimeSpan.FromMilliseconds(300);

        AnimateBorderShape(
            Card,
            CameraCircleSize,
            CameraCircleSize,
            cardWidth,
            cardHeight,
            growBegin,
            growDuration,
            growEase,
            () =>
            {
                if (_isPanelVisible)
                {
                    ResetCardShape();
                }
            });

        AnimateBorderShape(
            CardShadow,
            CameraCircleSize + ShadowOverhang,
            CameraCircleSize + ShadowOverhang,
            cardWidth + ShadowOverhang,
            cardHeight + ShadowOverhang,
            growBegin,
            growDuration,
            growEase);

        // Содержимое проявляется, когда панель уже почти раскрылась.
        CardContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
            {
                BeginTime = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        _contentTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // Ободок кружка на старте ярче — так «камера» заметнее на любом фоне.
        _cardBorderBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(ActiveBorderColor, IdleBorderColor, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void HidePanel()
    {
        if (!_isPanelVisible)
        {
            return;
        }

        _isPanelVisible = false;
        _outsideSinceUtc = null;
        _hotZoneSinceUtc = null;
        RootGrid.IsHitTestVisible = false;

        if (_isExpanded)
        {
            Collapse();
        }

        var startWidth = Card.ActualWidth > 0 ? Card.ActualWidth : CardSlotWidth();
        var startHeight = Card.ActualHeight > 0 ? Card.ActualHeight : CardSlotHeight();

        var shadowWidth = CardShadow.ActualWidth > 0 ? CardShadow.ActualWidth : startWidth + ShadowOverhang;
        var shadowHeight = CardShadow.ActualHeight > 0 ? CardShadow.ActualHeight : startHeight + ShadowOverhang;

        // Контент гаснет, пока карточка ещё достаточно широкая: к моменту,
        // когда остаётся кружок, внутри уже пусто. Форму стягиваем отдельно.
        CardContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(CardContent.Opacity, 0, TimeSpan.FromMilliseconds(110))
            {
                BeginTime = TimeSpan.FromMilliseconds(70),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            });

        var shrinkEase = new SineEase { EasingMode = EasingMode.EaseIn };
        var shrinkBegin = TimeSpan.FromMilliseconds(0);
        var shrinkDuration = TimeSpan.FromMilliseconds(260);

        // Ободок подсвечивается, пока форма стягивается обратно в кружок.
        _cardBorderBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(_cardBorderBrush.Color, ActiveBorderColor, shrinkDuration)
            {
                BeginTime = shrinkBegin,
                EasingFunction = shrinkEase
            });

        AnimateBorderShape(
            Card,
            startWidth,
            startHeight,
            CameraCircleSize,
            CameraCircleSize,
            shrinkBegin,
            shrinkDuration,
            shrinkEase);

        AnimateBorderShape(
            CardShadow,
            shadowWidth,
            shadowHeight,
            CameraCircleSize + ShadowOverhang,
            CameraCircleSize + ShadowOverhang,
            shrinkBegin,
            shrinkDuration,
            shrinkEase);

        // Кружок гаснет — и окно снова становится прозрачным для мыши.
        var fade = new DoubleAnimation(RootGrid.Opacity, 0, TimeSpan.FromMilliseconds(110))
        {
            BeginTime = TimeSpan.FromMilliseconds(245),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (!_isPanelVisible)
            {
                SetClickThrough(true);
                ResetCardShape();
            }
        };

        RootGrid.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Анимирует размер бордера (карточки или её тени). Скругление не трогаем:
    /// WPF сам ограничивает радиус половиной меньшей стороны, поэтому 34×34
    /// с радиусом 22 рисуется как ровный круг, а по мере роста — как карточка.
    /// </summary>
    private static void AnimateBorderShape(
        Border border,
        double fromWidth,
        double fromHeight,
        double toWidth,
        double toHeight,
        TimeSpan beginTime,
        TimeSpan duration,
        IEasingFunction easing,
        Action? completed = null)
    {
        var width = new DoubleAnimation(fromWidth, toWidth, duration)
        {
            BeginTime = beginTime,
            EasingFunction = easing
        };

        if (completed is not null)
        {
            width.Completed += (_, _) => completed();
        }

        border.BeginAnimation(FrameworkElement.WidthProperty, width);
        border.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(fromHeight, toHeight, duration)
            {
                BeginTime = beginTime,
                EasingFunction = easing
            });
    }

    /// <summary>Возвращает карточке «растянутый» вид на всю ширину окна.</summary>
    private void ResetCardShape()
    {
        Card.BeginAnimation(FrameworkElement.WidthProperty, null);
        Card.BeginAnimation(FrameworkElement.HeightProperty, null);
        Card.BeginAnimation(Border.CornerRadiusProperty, null);
        Card.ClearValue(FrameworkElement.WidthProperty);
        Card.ClearValue(FrameworkElement.HeightProperty);
        Card.CornerRadius = new CornerRadius(CardCornerRadius);

        CardShadow.BeginAnimation(FrameworkElement.WidthProperty, null);
        CardShadow.BeginAnimation(FrameworkElement.HeightProperty, null);
        CardShadow.BeginAnimation(Border.CornerRadiusProperty, null);
        CardShadow.ClearValue(FrameworkElement.WidthProperty);
        CardShadow.ClearValue(FrameworkElement.HeightProperty);
        CardShadow.CornerRadius = new CornerRadius(ShadowCornerRadius);

        CardContent.BeginAnimation(OpacityProperty, null);
        CardContent.Opacity = 1;

        _contentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _contentTranslate.Y = 0;

        _cardBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _cardBorderBrush.Color = IdleBorderColor;
    }

    private double CardSlotWidth() =>
        Math.Max(CameraCircleSize, Width - Card.Margin.Left - Card.Margin.Right);

    private double CardSlotHeight() =>
        Math.Max(CameraCircleSize, CompactWindowHeight - Card.Margin.Top - Card.Margin.Bottom);

    private bool IsCursorInHotZone()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var centerX = (info.Monitor.Left + info.Monitor.Right) / 2.0;
        var halfWidth = HotZoneHalfWidth * _dpiScale;
        var top = info.Monitor.Top;
        var bottom = top + HotZoneHeight * _dpiScale;

        return cursor.X >= centerX - halfWidth &&
               cursor.X <= centerX + halfWidth &&
               cursor.Y >= top &&
               cursor.Y <= bottom;
    }

    private bool IsCursorOverPanel()
    {
        if (!_isPanelVisible)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(handle, out var rect))
        {
            return false;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return false;
        }

        return cursor.X >= rect.Left &&
               cursor.X < rect.Right &&
               cursor.Y >= rect.Top &&
               cursor.Y < rect.Bottom;
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
        var updated = enabled
            ? style | NativeMethods.WsExTransparent
            : style & ~NativeMethods.WsExTransparent;

        if (updated != style)
        {
            NativeMethods.SetWindowLong(handle, NativeMethods.GwlExStyle, updated);
        }
    }

    // ===== данные =====

    private void RefreshUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshUi));
            return;
        }

        var snapshot = _media.Current;
        var hasSession = snapshot.HasSession;

        var title = hasSession
            ? (string.IsNullOrWhiteSpace(snapshot.Title) ? snapshot.SourceApp : snapshot.Title)
            : "Ничего не воспроизводится";

        var subtitle = hasSession
            ? FirstNonEmpty(snapshot.Artist, snapshot.Album, snapshot.SourceApp)
            : "Запустите музыку или видео";

        CompactTitle.Text = string.IsNullOrWhiteSpace(title) ? "Медиа" : title;
        CompactArtist.Text = subtitle;
        ExpandedTitle.Text = CompactTitle.Text;
        ExpandedArtist.Text = hasSession ? subtitle : "Нет активной медиасессии";

        CompactArt.Source = snapshot.Artwork;
        BigArt.Source = snapshot.Artwork;
        CompactArtPlaceholder.Visibility = snapshot.Artwork is null ? Visibility.Visible : Visibility.Collapsed;
        BigArtPlaceholder.Visibility = snapshot.Artwork is null ? Visibility.Visible : Visibility.Collapsed;

        var glyph = snapshot.IsPlaying ? PauseGlyph : PlayGlyph;
        CompactPlayGlyph.Text = glyph;
        PlayPauseGlyph.Text = glyph;

        CompactPlayButton.IsEnabled = hasSession;
        CompactPrevButton.IsEnabled = hasSession;
        CompactNextButton.IsEnabled = hasSession;
        PlayButton.IsEnabled = hasSession;

        _durationSeconds = snapshot.Duration.TotalSeconds;
        SeekSlider.Maximum = Math.Max(_durationSeconds, 1);
        SeekSlider.IsEnabled = hasSession && snapshot.CanSeek && _durationSeconds > 0;
        DurationText.Text = FormatTime(snapshot.Duration);

        if (!_isSeeking)
        {
            SeekSlider.Value = _durationSeconds > 0
                ? Math.Clamp(snapshot.Position.TotalSeconds, 0, _durationSeconds)
                : 0;
            PositionText.Text = FormatTime(snapshot.Position);
        }
    }

    private void UpdateProgress()
    {
        if (!_isExpanded || _isSeeking)
        {
            return;
        }

        var snapshot = _media.Current;
        if (!snapshot.HasSession || _durationSeconds <= 0)
        {
            return;
        }

        var position = _media.GetPosition();
        SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, _durationSeconds);
        PositionText.Text = FormatTime(position);
    }

    // ===== команды =====

    private void HeaderClickArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isExpanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _ = _media.TogglePlayPauseAsync();
        RestartAutoCollapse();
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        _ = _media.SkipPreviousAsync();
        RestartAutoCollapse();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _ = _media.SkipNextAsync();
        RestartAutoCollapse();
    }

    // ===== анимации раскрытия =====

    private void Expand()
    {
        if (_isExpanded)
        {
            return;
        }

        _isExpanded = true;
        _lastInteractionUtc = DateTime.UtcNow;

        // Если анимация появления ещё доигрывает — сразу отдаём карточке полный размер.
        ResetCardShape();

        ExpandedPanel.BeginAnimation(OpacityProperty, null);
        ExpandedPanel.Opacity = 1;
        ExpandedPanel.Visibility = Visibility.Visible;

        AnimateExpandedContent();
        AnimateWindowHeight(ExpandedWindowHeight);
        RestartAutoCollapse();
    }

    private void Collapse()
    {
        if (!_isExpanded)
        {
            return;
        }

        _isExpanded = false;
        _isSeeking = false;
        _autoCollapseTimer.Stop();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (!_isExpanded)
            {
                ExpandedPanel.Visibility = Visibility.Collapsed;
            }
        };

        ExpandedPanel.BeginAnimation(OpacityProperty, fade);
        AnimateWindowHeight(CompactWindowHeight);
    }

    private void AnimateExpandedContent()
    {
        AnimateIn(ExpandedArtHost, _artTranslate, _artScale, 0.92, 20, 18);
        AnimateIn(ExpandedTextBlock, _titleTranslate, null, 1, 90, 12);
        AnimateIn(ExpandedSeekBlock, _seekTranslate, null, 1, 150, 12);
        AnimateIn(ExpandedControls, _controlsTranslate, null, 1, 210, 10);
    }

    private static void AnimateIn(
        UIElement element,
        TranslateTransform translate,
        ScaleTransform? scale,
        double fromScale,
        int delayMs,
        double offset)
    {
        element.Opacity = 0;

        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(340))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        if (scale is not null)
        {
            var scaleAnimation = new DoubleAnimation(fromScale, 1, TimeSpan.FromMilliseconds(360))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateWindowHeight(double target)
    {
        BeginAnimation(
            HeightProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(340))
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void RestartAutoCollapse()
    {
        if (!_isExpanded)
        {
            return;
        }

        _lastInteractionUtc = DateTime.UtcNow;
        if (!_autoCollapseTimer.IsEnabled)
        {
            _autoCollapseTimer.Start();
        }
    }

    private void RepositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 8;
    }

    // ===== перемотка =====

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            PositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled)
        {
            return;
        }

        _isSeeking = true;
        _autoCollapseTimer.Stop();
    }

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CommitSeek();

    private void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e) => CommitSeek();

    private void CommitSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        var target = TimeSpan.FromSeconds(SeekSlider.Value);
        PositionText.Text = FormatTime(target);
        _ = _media.SeekAsync(target);
        RestartAutoCollapse();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}

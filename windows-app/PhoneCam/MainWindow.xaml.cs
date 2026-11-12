using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneCam.Logging;
using PhoneCam.Net;
using PhoneCam.Protocol;
using PhoneCam.Settings;
using PhoneCam.UI;
using PhoneCam.Video;
using Color = System.Windows.Media.Color;

namespace PhoneCam;

public partial class MainWindow : Window
{
    private readonly DebugLogger _logger = new();
    private readonly AppSettings _settings;
    private readonly TcpStreamServer _server;
    private readonly UdpDiscoveryResponder _discovery;
    private readonly MediaFoundationH264Decoder _decoder;
    private readonly SharedFrameWriter _sharedWriter;
    private readonly VirtualCameraHost _virtualCam;
    private readonly SystemTrayIcon _trayIcon;

    private readonly object _frameLock = new();
    private byte[] _backBgra = Array.Empty<byte>();
    private byte[] _frontBgra = Array.Empty<byte>();
    private byte[] _nv12Scratch = Array.Empty<byte>();
    private int _frameWidth;
    private int _frameHeight;
    private long _latestPhoneTsUs;
    private double _latestDecodeMs;
    private int _renderPending;

    private WriteableBitmap? _bitmap;
    private int _streamRotationQuarters;
    private double _lastRttMs;
    private bool _loggedFirstFrame;
    private bool _uiReady;
    private bool _isExiting;
    private bool _syncedSettingsOnConnect;

    private long _bytesSinceLastStat;
    private int _framesSinceLastStat;
    private long _statWindowStartTick = Stopwatch.GetTimestamp();
    private double _currentFps;
    private double _currentMbps;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _server = new TcpStreamServer(_logger);
        _discovery = new UdpDiscoveryResponder(_logger);
        _decoder = new MediaFoundationH264Decoder(_logger);
        _sharedWriter = new SharedFrameWriter(_logger);
        _virtualCam = new VirtualCameraHost(_logger);
        _trayIcon = new SystemTrayIcon();

        _trayIcon.ShowRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);
        _trayIcon.SwitchCameraRequested += () => Dispatcher.BeginInvoke(() => BtnSwitchCam_Click(this, new RoutedEventArgs()));
        _trayIcon.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);

        _logger.Updated += OnLoggerUpdated;
        _server.StatusChanged += OnServerStatusChanged;
        _server.ClientDisconnected += OnClientDisconnected;
        _server.StreamInfoReceived += OnStreamInfoReceived;
        _server.CodecConfigReceived += OnCodecConfigReceived;
        _server.VideoFrameReceived += OnVideoFrameReceived;
        _server.RttUpdated += rtt => _lastRttMs = rtt;

        ApplySettingsToUi();
        _uiReady = true;

        Loaded += async (_, _) =>
        {
            _sharedWriter.Initialize();
            _server.Start(WireProtocol.DefaultPort);
            _discovery.Start(WireProtocol.DefaultPort);

            var (_, vcamStatus) = await _virtualCam.StartAsync();
            TxtVirtualCam.Text = vcamStatus;
            TxtVirtualCam.Foreground = _virtualCam.IsActive
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                : new SolidColorBrush(Color.FromRgb(251, 191, 36));
            BtnVirtualCam.Content = _virtualCam.IsActive ? "VCam: ON" : "VCam: Retry";

            var (ok, msg) = await AdbHelper.TrySetupReverseAsync(WireProtocol.DefaultPort, _logger);
            if (!ok)
            {
                _logger.Log($"USB adb reverse skipped: {msg}", force: true);
            }
        };
        Closing += OnWindowClosing;
    }

    private void ApplySettingsToUi()
    {
        string resTag = $"{_settings.Width}x{_settings.Height}";
        foreach (ComboBoxItem item in CmbResolution.Items)
        {
            if (item.Tag?.ToString() == resTag)
            {
                CmbResolution.SelectedItem = item;
                break;
            }
        }
        if (CmbResolution.SelectedItem == null) CmbResolution.SelectedIndex = 1;

        string fpsTag = _settings.Fps.ToString();
        foreach (ComboBoxItem item in CmbFps.Items)
        {
            if (item.Tag?.ToString() == fpsTag)
            {
                CmbFps.SelectedItem = item;
                break;
            }
        }
        if (CmbFps.SelectedItem == null) CmbFps.SelectedIndex = 2;

        SldBitrate.Value = Math.Clamp(_settings.BitrateMbps, 2, 16);
        TxtBitrateValue.Text = $"{(int)SldBitrate.Value} Mbps";

        SldZoom.Value = Math.Clamp(_settings.Zoom, 1.0, 5.0);
        TxtZoomValue.Text = $"{SldZoom.Value:F1}x";

        BtnSwitchCam.Content = _settings.UseFrontCamera ? "Cam: Front" : "Cam: Back";
        ChkMirrorH.IsChecked = _settings.MirrorHorizontal;
        ChkFlipV.IsChecked = _settings.FlipVertical;
        ChkMinimizeToTray.IsChecked = _settings.MinimizeToTrayOnClose;
        ChkDebugLog.IsChecked = _settings.DebugLogEnabled;

        _logger.Enabled = _settings.DebugLogEnabled;
        LogPanel.Visibility = _settings.DebugLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyTransform();
    }

    private void OnLoggerUpdated()
    {
        if (!_logger.Enabled) return;
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.Text = _logger.GetAllText();
            TxtLog.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnServerStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtStatus.Text = status;
            _trayIcon.UpdateStatus($"PhoneCam — {status}");
        });
    }

    private void OnClientDisconnected()
    {
        _syncedSettingsOnConnect = false;
        _sharedWriter.ClearActiveFrame();
        Interlocked.Exchange(ref _framesSinceLastStat, 0);
        Interlocked.Exchange(ref _bytesSinceLastStat, 0);
        _currentFps = 0;
        _currentMbps = 0;
        _lastRttMs = 0;
        Dispatcher.BeginInvoke(() => TxtStats.Text = "Waiting for stream...");
    }

    private async void OnStreamInfoReceived(WireProtocol.StreamInfo info)
    {
        try
        {
            _streamRotationQuarters = ((info.RotationQuarters % 4) + 4) % 4;
            ApplyTransform();

            if (!_decoder.IsInitialized || _decoder.InputWidth != info.Width || _decoder.InputHeight != info.Height)
            {
                _loggedFirstFrame = false;
                _decoder.Initialize(info.Width, info.Height, Math.Max(1, info.Fps));
            }

            if (!_syncedSettingsOnConnect)
            {
                _syncedSettingsOnConnect = true;
                bool differs =
                    info.Width != _settings.Width ||
                    info.Height != _settings.Height ||
                    info.Fps != _settings.Fps ||
                    info.Bitrate != (uint)(_settings.BitrateMbps * 1_000_000) ||
                    _settings.UseFrontCamera ||
                    Math.Abs(_settings.Zoom - 1.0) > 0.05;

                if (differs)
                {
                    await _server.SendConfigAsync(
                        width: _settings.Width,
                        height: _settings.Height,
                        fps: _settings.Fps,
                        bitrateBps: (uint)(_settings.BitrateMbps * 1_000_000),
                        useFront: _settings.UseFrontCamera,
                        zoom: _settings.Zoom);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Decoder init error: {ex.Message}", force: true);
            _ = Dispatcher.BeginInvoke(() => TxtStatus.Text = $"Decoder error: {ex.Message}");
        }
    }

    private void ApplyTransform()
    {
        int totalQuarters = (_streamRotationQuarters + _settings.ManualRotationOffsetQuarters) % 4;
        _decoder.SetTransform(totalQuarters, _settings.MirrorHorizontal, _settings.FlipVertical);
        _ = Dispatcher.BeginInvoke(() =>
        {
            BtnRotate.Content = _settings.ManualRotationOffsetQuarters == 0
                ? $"Rot: Auto ({_streamRotationQuarters * 90}°)"
                : $"Rot: +{_settings.ManualRotationOffsetQuarters * 90}°";
        });
    }

    private void OnCodecConfigReceived(byte[] buffer, int length, long timestampUs)
    {
        _decoder.SetCodecConfig(buffer.AsSpan(0, length));
    }

    private void OnVideoFrameReceived(byte[] buffer, int length, long phoneTsUs, bool isKeyframe)
    {
        Interlocked.Add(ref _bytesSinceLastStat, WireProtocol.HeaderSize + length);

        long t0 = Stopwatch.GetTimestamp();
        bool decoded;
        int w;
        int h;

        try
        {
            decoded = _decoder.DecodeFrame(
                buffer.AsSpan(0, length),
                isKeyframe,
                EnsureBackBuffer,
                _sharedWriter.IsReady ? EnsureNv12Buffer : null,
                out w,
                out h);
        }
        catch (Exception ex)
        {
            _logger.Log($"Decode exception: {ex.Message}", force: true);
            return;
        }

        if (!decoded || w <= 0 || h <= 0) return;

        if (_sharedWriter.IsReady && _nv12Scratch.Length >= w * h * 3 / 2)
        {
            _sharedWriter.WriteNv12Frame(_nv12Scratch, w, h, phoneTsUs);
        }

        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            _logger.Log($"First frame decoded: {w}x{h} (hw={_decoder.IsHardwareDecode})", force: true);
        }

        double decodeMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

        lock (_frameLock)
        {
            int required = w * h * 4;
            if (_frontBgra.Length != required)
            {
                _frontBgra = new byte[required];
            }
            (_frontBgra, _backBgra) = (_backBgra, _frontBgra);
            _frameWidth = w;
            _frameHeight = h;
            _latestPhoneTsUs = phoneTsUs;
            _latestDecodeMs = decodeMs;
        }

        Interlocked.Increment(ref _framesSinceLastStat);

        if (Interlocked.CompareExchange(ref _renderPending, 1, 0) == 0)
        {
            Dispatcher.BeginInvoke(RenderLatestFrame, DispatcherPriority.Render);
        }
    }

    private byte[] EnsureBackBuffer(int width, int height)
    {
        int needed = width * height * 4;
        if (_backBgra.Length != needed)
        {
            _backBgra = new byte[needed];
        }
        return _backBgra;
    }

    private byte[] EnsureNv12Buffer(int width, int height)
    {
        int needed = width * height * 3 / 2;
        if (_nv12Scratch.Length != needed)
        {
            _nv12Scratch = new byte[needed];
        }
        return _nv12Scratch;
    }

    private void RenderLatestFrame()
    {
        Interlocked.Exchange(ref _renderPending, 0);

        int w;
        int h;
        long phoneTsUs;
        double decodeMs;

        lock (_frameLock)
        {
            w = _frameWidth;
            h = _frameHeight;
            phoneTsUs = _latestPhoneTsUs;
            decodeMs = _latestDecodeMs;
            if (w <= 0 || h <= 0 || _frontBgra.Length < w * h * 4) return;

            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                VideoPreview.Source = _bitmap;
            }

            _bitmap.Lock();
            try
            {
                unsafe
                {
                    fixed (byte* src = _frontBgra)
                    {
                        long byteCount = (long)w * h * 4;
                        Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer, byteCount, byteCount);
                    }
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally
            {
                _bitmap.Unlock();
            }
        }

        UpdateStatsText(w, h, phoneTsUs, decodeMs);
    }

    private void UpdateStatsText(int w, int h, long phoneTsUs, double decodeMs)
    {
        long nowTick = Stopwatch.GetTimestamp();
        double elapsedSec = (nowTick - _statWindowStartTick) / (double)Stopwatch.Frequency;
        if (elapsedSec >= 0.5)
        {
            int frames = Interlocked.Exchange(ref _framesSinceLastStat, 0);
            long bytes = Interlocked.Exchange(ref _bytesSinceLastStat, 0);
            _currentFps = frames / elapsedSec;
            _currentMbps = (bytes * 8.0) / (elapsedSec * 1_000_000.0);
            _statWindowStartTick = nowTick;
        }

        string pipelineStr = _server.TryEstimateFrameAgeMs(phoneTsUs, out double ageMs)
            ? $"{ageMs:F1} ms"
            : "syncing...";

        string mode = _decoder.IsHardwareDecode ? "D3D11 HW" : "CPU SW";
        TxtStats.Text =
            $"{w}x{h} | {_currentFps:F1} fps | {_currentMbps:F2} Mbps | {mode} | " +
            $"Decode: {decodeMs:F1} ms | Net RTT: {_lastRttMs:F1} ms | Est E2E: {pipelineStr}";
    }

    private async void BtnVirtualCam_Click(object sender, RoutedEventArgs e)
    {
        if (_virtualCam.IsActive)
        {
            _virtualCam.Stop();
            TxtVirtualCam.Text = "Virtual Cam: Off";
            TxtVirtualCam.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            BtnVirtualCam.Content = "VCam: OFF";
        }
        else
        {
            var (_, status) = await _virtualCam.StartAsync();
            TxtVirtualCam.Text = status;
            TxtVirtualCam.Foreground = _virtualCam.IsActive
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                : new SolidColorBrush(Color.FromRgb(251, 191, 36));
            BtnVirtualCam.Content = _virtualCam.IsActive ? "VCam: ON" : "VCam: Retry";
        }
    }

    private async void BtnSwitchCam_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseFrontCamera = !_settings.UseFrontCamera;
        _settings.Save();
        BtnSwitchCam.Content = _settings.UseFrontCamera ? "Cam: Front" : "Cam: Back";
        await _server.SetCameraFacingAsync(_settings.UseFrontCamera);
    }

    private async void BtnApplyVideo_Click(object sender, RoutedEventArgs e)
    {
        if (CmbResolution.SelectedItem is ComboBoxItem resItem && resItem.Tag is string resTag)
        {
            var parts = resTag.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                _settings.Width = w;
                _settings.Height = h;
            }
        }
        if (CmbFps.SelectedItem is ComboBoxItem fpsItem && int.TryParse(fpsItem.Tag?.ToString(), out int fps))
        {
            _settings.Fps = fps;
        }
        _settings.Save();
        await _server.SendConfigAsync(width: _settings.Width, height: _settings.Height, fps: _settings.Fps);
    }

    private async void SldBitrate_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        int mbps = (int)SldBitrate.Value;
        TxtBitrateValue.Text = $"{mbps} Mbps";
        _settings.BitrateMbps = mbps;
        _settings.Save();
        await _server.SendConfigAsync(bitrateBps: (uint)(mbps * 1_000_000));
    }

    private async void SldZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        double zoom = Math.Round(SldZoom.Value, 1);
        TxtZoomValue.Text = $"{zoom:F1}x";
        _settings.Zoom = zoom;
        _settings.Save();
        await _server.SendConfigAsync(zoom: zoom);
    }

    private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
    {
        SldZoom.Value = 1.0;
    }

    private void TransformOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.MirrorHorizontal = ChkMirrorH.IsChecked == true;
        _settings.FlipVertical = ChkFlipV.IsChecked == true;
        _settings.Save();
        ApplyTransform();
    }

    private void ChkMinimizeToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.MinimizeToTrayOnClose = ChkMinimizeToTray.IsChecked == true;
        _settings.Save();
    }

    private void BtnHideToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void BtnSettings_Changed(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = BtnSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BtnAdbReverse_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = await AdbHelper.TrySetupReverseAsync(WireProtocol.DefaultPort, _logger);
        TxtStatus.Text = msg;
    }

    private void BtnRotate_Click(object sender, RoutedEventArgs e)
    {
        _settings.ManualRotationOffsetQuarters = (_settings.ManualRotationOffsetQuarters + 1) % 4;
        _settings.Save();
        ApplyTransform();
    }

    private async void BtnKeyframe_Click(object sender, RoutedEventArgs e)
    {
        await _server.RequestKeyframeAsync();
    }

    private void ChkDebugLog_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        bool enabled = ChkDebugLog.IsChecked == true;
        _settings.DebugLogEnabled = enabled;
        _settings.Save();
        _logger.Enabled = enabled;
        LogPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            TxtLog.Text = _logger.GetAllText();
            TxtLog.ScrollToEnd();
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => _logger.Clear();

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting && _settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _trayIcon.Dispose();
        _virtualCam.Dispose();
        _sharedWriter.Dispose();
        await _discovery.DisposeAsync();
        await _server.DisposeAsync();
        _decoder.Dispose();
    }
}

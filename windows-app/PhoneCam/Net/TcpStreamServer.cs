using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using PhoneCam.Logging;
using PhoneCam.Protocol;

namespace PhoneCam.Net;

public sealed class TcpStreamServer : IAsyncDisposable
{
    private readonly DebugLogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpListener? _listener;
    private NetworkStream? _activeStream;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private uint _txSequence;
    private int _listeningPort = WireProtocol.DefaultPort;
    private long _lastPacketTickMs;

    private readonly object _clockLock = new();
    private bool _hasClockSync;
    private double _clockOffsetUs;
    private double _bestRttUs = double.MaxValue;
    private double _lastRttMs;

    public event Action<string>? StatusChanged;
    public event Action? ClientDisconnected;
    public event Action<WireProtocol.StreamInfo>? StreamInfoReceived;
    public event Action<byte[], int, long>? CodecConfigReceived;
    public event Action<byte[], int, long, bool>? VideoFrameReceived;
    public event Action<byte[], int, long>? AudioFrameReceived;
    public event Action<double>? RttUpdated;

    public TcpStreamServer(DebugLogger logger)
    {
        _logger = logger;
    }

    public static long GetPcMonotonicUs() =>
        (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    public bool TryEstimateFrameAgeMs(long phoneFrameTsUs, out double ageMs)
    {
        lock (_clockLock)
        {
            if (!_hasClockSync)
            {
                ageMs = 0;
                return false;
            }
            var nowPcUs = GetPcMonotonicUs();
            var framePcUs = phoneFrameTsUs - _clockOffsetUs;
            ageMs = Math.Max(0.0, (nowPcUs - framePcUs) / 1000.0);
            return true;
        }
    }

    public void Start(int port = WireProtocol.DefaultPort)
    {
        if (_cts != null) return;
        _listeningPort = port;
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            var waitingText = BuildWaitingStatus();
            StatusChanged?.Invoke(waitingText);
            _logger.Log($"TCP server listening on 0.0.0.0:{port}", force: true);
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            string msg = $"Port {port} already in use — close any other PhoneCam instance.";
            StatusChanged?.Invoke(msg);
            _logger.Log(msg, force: true);
        }
        catch (Exception ex)
        {
            string msg = $"Failed to listen on port {port}: {ex.Message}";
            StatusChanged?.Invoke(msg);
            _logger.Log(msg, force: true);
        }
    }

    private string BuildWaitingStatus()
    {
        var ips = UdpDiscoveryResponder.GetLocalIpv4Addresses();
        return ips.Count > 0
            ? $"Ready | WiFi IP: {string.Join(", ", ips)}"
            : "Ready (USB Cable)";
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                client.ReceiveBufferSize = 512 * 1024;

                ResetClockSync();
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
                var transport = remoteIp is "127.0.0.1" or "::1" ? "USB" : $"WiFi ({remoteIp})";
                StatusChanged?.Invoke($"Connected via {transport}");
                _logger.Log($"Accepted {transport} connection from {client.Client.RemoteEndPoint}", force: true);

                await HandleClientAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Log($"Accept error: {ex.Message}", force: true);
                StatusChanged?.Invoke($"Listener error: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken outerCt)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        _activeStream = stream;
        Volatile.Write(ref _lastPacketTickMs, Environment.TickCount64);

        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var pingTask = Task.Run(() => PingAndWatchdogLoopAsync(clientCts), clientCts.Token);

        var headerBuf = new byte[WireProtocol.HeaderSize];
        var payloadBuf = new byte[512 * 1024];

        try
        {
            await SendControlAsync(WireProtocol.TypeRequestKeyframe, 0, GetPcMonotonicUs(), ReadOnlyMemory<byte>.Empty, clientCts.Token)
                .ConfigureAwait(false);

            while (!clientCts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(headerBuf, clientCts.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastPacketTickMs, Environment.TickCount64);

                var hdr = WireProtocol.ReadHeader(headerBuf);
                if (hdr.Magic != WireProtocol.Magic || hdr.Version != WireProtocol.Version)
                {
                    throw new InvalidDataException($"Invalid packet header magic=0x{hdr.Magic:X4} ver={hdr.Version}");
                }
                if (hdr.PayloadLength > WireProtocol.MaxPayloadBytes)
                {
                    throw new InvalidDataException($"Payload too large: {hdr.PayloadLength}");
                }

                var len = (int)hdr.PayloadLength;
                if (len > payloadBuf.Length)
                {
                    payloadBuf = new byte[len];
                }
                if (len > 0)
                {
                    await stream.ReadExactlyAsync(payloadBuf.AsMemory(0, len), clientCts.Token).ConfigureAwait(false);
                    Volatile.Write(ref _lastPacketTickMs, Environment.TickCount64);
                }

                switch (hdr.Type)
                {
                    case WireProtocol.TypeStreamInfo when len >= 16:
                        var info = WireProtocol.ReadStreamInfo(payloadBuf.AsSpan(0, 16));
                        _logger.Log($"STREAM_INFO: {info.Width}x{info.Height} @ {info.Fps}fps, rot={info.RotationQuarters * 90}, bitrate={info.Bitrate}", force: true);
                        StreamInfoReceived?.Invoke(info);
                        break;

                    case WireProtocol.TypeCodecConfig:
                        _logger.Log($"CODEC_CONFIG: {len} bytes");
                        CodecConfigReceived?.Invoke(payloadBuf, len, hdr.TimestampUs);
                        break;

                    case WireProtocol.TypeVideoFrame:
                        var isKeyframe = (hdr.Flags & WireProtocol.FlagKeyframe) != 0;
                        VideoFrameReceived?.Invoke(payloadBuf, len, hdr.TimestampUs, isKeyframe);
                        break;

                    case WireProtocol.TypeAudioFrame when len > WireProtocol.AudioFrameHeaderSize:
                        AudioFrameReceived?.Invoke(payloadBuf, len, hdr.TimestampUs);
                        break;

                    case WireProtocol.TypePong when len >= 8:
                        HandlePong(hdr.TimestampUs, BinaryPrimitives.ReadInt64BigEndian(payloadBuf.AsSpan(0, 8)));
                        break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            _logger.Log("Phone disconnected (EOF)", force: true);
        }
        catch (OperationCanceledException)
        {
            if (!outerCt.IsCancellationRequested)
            {
                _logger.Log("Phone timed out (no packets for 4.5s)", force: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Stream error: {ex.Message}", force: true);
        }
        finally
        {
            _activeStream = null;
            await clientCts.CancelAsync().ConfigureAwait(false);
            try { await pingTask.ConfigureAwait(false); } catch { }
            ClientDisconnected?.Invoke();
            if (!outerCt.IsCancellationRequested)
            {
                StatusChanged?.Invoke($"Disconnected — {BuildWaitingStatus()}");
            }
        }
    }

    private async Task PingAndWatchdogLoopAsync(CancellationTokenSource clientCts)
    {
        var ct = clientCts.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastPacketTickMs) > 4500)
            {
                await clientCts.CancelAsync().ConfigureAwait(false);
                break;
            }
            await SendControlAsync(WireProtocol.TypePing, 0, GetPcMonotonicUs(), ReadOnlyMemory<byte>.Empty, ct)
                .ConfigureAwait(false);
        }
    }

    private void HandlePong(long pcSendTsUs, long phoneRxTsUs)
    {
        var nowPcUs = GetPcMonotonicUs();
        var rttUs = Math.Max(0.0, nowPcUs - pcSendTsUs);
        var sampleOffsetUs = phoneRxTsUs - (pcSendTsUs + rttUs * 0.5);

        lock (_clockLock)
        {
            _lastRttMs = rttUs / 1000.0;
            if (!_hasClockSync)
            {
                _clockOffsetUs = sampleOffsetUs;
                _bestRttUs = rttUs;
                _hasClockSync = true;
            }
            else
            {
                if (rttUs <= _bestRttUs * 1.25)
                {
                    _bestRttUs = Math.Min(_bestRttUs, rttUs);
                    _clockOffsetUs = _clockOffsetUs * 0.75 + sampleOffsetUs * 0.25;
                }
                else
                {
                    _bestRttUs = _bestRttUs * 0.98 + rttUs * 0.02;
                    _clockOffsetUs = _clockOffsetUs * 0.95 + sampleOffsetUs * 0.05;
                }
            }
        }
        RttUpdated?.Invoke(_lastRttMs);
    }

    private void ResetClockSync()
    {
        lock (_clockLock)
        {
            _hasClockSync = false;
            _clockOffsetUs = 0;
            _bestRttUs = double.MaxValue;
            _lastRttMs = 0;
        }
    }

    public Task RequestKeyframeAsync(CancellationToken ct = default) =>
        SendControlAsync(WireProtocol.TypeRequestKeyframe, 0, GetPcMonotonicUs(), ReadOnlyMemory<byte>.Empty, ct);

    public Task SetAudioEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        var payload = new byte[4];
        WireProtocol.WriteAudioControl(payload, enabled);
        return SendControlAsync(WireProtocol.TypeAudioControl, 0, GetPcMonotonicUs(), payload, ct);
    }

    public Task SetCameraFacingAsync(bool useFront, CancellationToken ct = default)
    {
        var payload = new byte[16];
        WireProtocol.WriteSetConfig(
            payload,
            cameraFacing: useFront ? WireProtocol.CameraFacingFront : WireProtocol.CameraFacingBack);
        return SendControlAsync(WireProtocol.TypeSetConfig, 0, GetPcMonotonicUs(), payload, ct);
    }

    public Task SendConfigAsync(
        int? width = null,
        int? height = null,
        int? fps = null,
        uint? bitrateBps = null,
        bool? useFront = null,
        double? zoom = null,
        CancellationToken ct = default)
    {
        var payload = new byte[16];
        WireProtocol.WriteSetConfig(
            payload,
            width: (ushort)(width ?? 0),
            height: (ushort)(height ?? 0),
            fps: (ushort)(fps ?? 0),
            bitrate: bitrateBps ?? 0,
            cameraFacing: useFront.HasValue
                ? (useFront.Value ? WireProtocol.CameraFacingFront : WireProtocol.CameraFacingBack)
                : WireProtocol.CameraFacingUnchanged,
            zoomX100: zoom.HasValue ? (ushort)Math.Clamp((int)Math.Round(zoom.Value * 100.0), 50, 1000) : (ushort)0);
        return SendControlAsync(WireProtocol.TypeSetConfig, 0, GetPcMonotonicUs(), payload, ct);
    }

    private async Task SendControlAsync(
        byte type,
        ushort flags,
        long timestampUs,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct)
    {
        var stream = _activeStream;
        if (stream == null) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_activeStream != stream) return;
            var seq = Interlocked.Increment(ref _txSequence) - 1;
            var packet = ArrayPool<byte>.Shared.Rent(WireProtocol.HeaderSize + payload.Length);
            try
            {
                WireProtocol.WriteHeader(packet.AsSpan(0, WireProtocol.HeaderSize), type, flags, seq, (uint)payload.Length, timestampUs);
                if (!payload.IsEmpty)
                {
                    payload.Span.CopyTo(packet.AsSpan(WireProtocol.HeaderSize));
                }
                await stream.WriteAsync(packet.AsMemory(0, WireProtocol.HeaderSize + payload.Length), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        catch
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        try { _listener?.Stop(); } catch { }
        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}

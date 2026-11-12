using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PhoneCam.Logging;
using PhoneCam.Protocol;

namespace PhoneCam.Net;

public sealed class UdpDiscoveryResponder : IAsyncDisposable
{
    private readonly DebugLogger _logger;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public UdpDiscoveryResponder(DebugLogger logger)
    {
        _logger = logger;
    }

    public void Start(int tcpPort = WireProtocol.DefaultPort)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        try
        {
            _udp = new UdpClient(WireProtocol.DiscoveryPort);
            _udp.EnableBroadcast = true;
            _logger.Log($"UDP discovery listening on 0.0.0.0:{WireProtocol.DiscoveryPort}", force: true);
            _listenTask = Task.Run(() => ListenLoopAsync(tcpPort, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Log($"UDP discovery start failed: {ex.Message}", force: true);
        }
    }

    private async Task ListenLoopAsync(int tcpPort, CancellationToken ct)
    {
        if (_udp == null) return;
        byte[] replyBytes = Encoding.UTF8.GetBytes(
            $"{WireProtocol.DiscoveryReplyPrefix}{Environment.MachineName}|{tcpPort}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (text == WireProtocol.DiscoveryProbe)
                {
                    await _udp.SendAsync(replyBytes, result.RemoteEndPoint, ct).ConfigureAwait(false);
                    _logger.Log($"Discovery probe from {result.RemoteEndPoint} -> replied");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Log($"UDP discovery error: {ex.Message}");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    public static List<string> GetLocalIpv4Addresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var desc = ni.Description;
                if (desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    result.Add(ip);
                }
            }
        }
        catch
        {
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        try { _udp?.Close(); } catch { }
        _udp?.Dispose();
        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}


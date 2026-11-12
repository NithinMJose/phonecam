using PhoneCam.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace PhoneCam.Video;

public unsafe sealed class VirtualCameraHost : IDisposable
{
    private const string SourceClsid = "{7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}";
    private const string FriendlyName = "PhoneCam";

    private readonly DebugLogger _logger;
    private readonly object _lock = new();
    private IMFVirtualCamera* _vcam;
    private bool _mfStarted;
    private bool _active;

    public bool IsActive => _active;

    public VirtualCameraHost(DebugLogger logger)
    {
        _logger = logger;
    }

    public Task<(bool Ok, string Status)> StartAsync() => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_active) return (true, "Virtual Cam: Active (PhoneCam)");

            try
            {
                if (!_mfStarted)
                {
                    PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
                    _mfStarted = true;
                }

                IMFVirtualCamera* vcam = null;
                HRESULT hr = PInvoke.MFCreateVirtualCamera(
                    MFVirtualCameraType.MFVirtualCameraType_SoftwareCameraSource,
                    MFVirtualCameraLifetime.MFVirtualCameraLifetime_Session,
                    MFVirtualCameraAccess.MFVirtualCameraAccess_CurrentUser,
                    FriendlyName,
                    SourceClsid,
                    ReadOnlySpan<Guid>.Empty,
                    &vcam);

                if (hr.Failed || vcam == null)
                {
                    return (false, $"Virtual Cam init failed (0x{hr.Value:X8})");
                }

                _vcam = vcam;
                _vcam->Start(null);
                _active = true;
                _logger.Log("Virtual Camera started: PhoneCam (Windows Virtual Camera)", force: true);
                return (true, "Virtual Cam: Active (PhoneCam)");
            }
            catch (Exception ex)
            {
                StopInternal();
                string hint = ex.HResult switch
                {
                    unchecked((int)0x80040154) =>
                        "Virtual Cam DLL not registered — run installer or setup-and-register.ps1 as Admin.",
                    unchecked((int)0x80070005) =>
                        "Camera privacy blocked Virtual Camera (Settings > Privacy & security > Camera).",
                    _ =>
                        $"Virtual Cam unavailable (0x{ex.HResult:X8}: {ex.Message})"
                };
                _logger.Log(hint, force: true);
                return (false, hint);
            }
        }
    });

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _active = false;
        if (_vcam != null)
        {
            try { _vcam->Stop(); } catch { }
            try { _vcam->Remove(); } catch { }
            try { _vcam->Shutdown(); } catch { }
            _vcam->Release();
            _vcam = null;
        }
        if (_mfStarted)
        {
            PInvoke.MFShutdown();
            _mfStarted = false;
        }
    }

    public void Dispose() => Stop();
}


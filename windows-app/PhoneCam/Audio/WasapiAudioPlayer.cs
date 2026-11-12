using System.Runtime.InteropServices;
using PhoneCam.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace PhoneCam.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsVirtualCable);

public unsafe sealed class WasapiAudioPlayer : IDisposable
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly PROPERTYKEY PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };
    private static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private const int RingCapacity = 48000 * 2;
    private const int MaxBufferedSamples = 4800;  // 100 ms cap
    private const int TrimTargetSamples = 1920;   // 40 ms after trim

    private readonly DebugLogger _logger;
    private readonly object _stateLock = new();
    private readonly object _ringLock = new();
    private readonly short[] _ring = new short[RingCapacity];

    private int _ringRead;
    private int _ringWrite;
    private int _ringCount;
    private int _inputSampleRate = 48000;
    private double _resamplePhase;
    private double _currentLevel;
    private double _gain = 1.0;
    private bool _muted;

    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private string? _targetDeviceId;
    private string _activeDeviceName = "";
    private volatile bool _active;

    public bool IsActive => _active;
    public string ActiveDeviceName => _activeDeviceName;
    public double CurrentLevel => Volatile.Read(ref _currentLevel);

    public double Gain
    {
        get => Volatile.Read(ref _gain);
        set => Volatile.Write(ref _gain, Math.Clamp(value, 0.0, 2.5));
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public WasapiAudioPlayer(DebugLogger logger)
    {
        _logger = logger;
    }

    public static bool IsVirtualCableName(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase);

    public static bool IsVirtualMicName(string name) =>
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Mic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase);

    public static List<AudioDeviceInfo> EnumerateRenderDevices() => EnumerateDevices(EDataFlow.eRender);

    public static string? DetectVirtualMicCaptureName()
    {
        foreach (var dev in EnumerateDevices(EDataFlow.eCapture))
        {
            if (IsVirtualMicName(dev.Name))
            {
                return dev.Name;
            }
        }
        return null;
    }

    private static List<AudioDeviceInfo> EnumerateDevices(EDataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        HRESULT coHr = PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
        bool didCoInit = coHr.Succeeded;

        IMMDeviceEnumerator* enumerator = null;
        IMMDeviceCollection* collection = null;
        try
        {
            var hr = PInvoke.CoCreateInstance(
                in ClsidMMDeviceEnumerator,
                null,
                CLSCTX.CLSCTX_ALL,
                out enumerator);
            if (hr.Failed || enumerator == null) return list;

            enumerator->EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, &collection);
            if (collection == null) return list;

            collection->GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* dev = null;
                IPropertyStore* props = null;
                PWSTR idPtr = default;
                PROPVARIANT pv = default;
                try
                {
                    collection->Item(i, &dev);
                    if (dev == null) continue;

                    dev->GetId(&idPtr);
                    string id = idPtr.Value != null ? new string(idPtr.Value) : "";

                    string name = id;
                    dev->OpenPropertyStore(STGM.STGM_READ, &props);
                    if (props != null)
                    {
                        var key = PkeyDeviceFriendlyName;
                        props->GetValue(in key, out pv);
                        if (pv.Anonymous.Anonymous.vt == Windows.Win32.System.Variant.VARENUM.VT_LPWSTR &&
                            pv.Anonymous.Anonymous.Anonymous.pwszVal.Value != null)
                        {
                            name = new string(pv.Anonymous.Anonymous.Anonymous.pwszVal.Value);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        bool isVirtual = flow == EDataFlow.eRender ? IsVirtualCableName(name) : IsVirtualMicName(name);
                        list.Add(new AudioDeviceInfo(id, name, isVirtual));
                    }
                }
                catch
                {
                }
                finally
                {
                    PInvoke.PropVariantClear(ref pv);
                    if (idPtr.Value != null) PInvoke.CoTaskMemFree(idPtr.Value);
                    if (props != null) props->Release();
                    if (dev != null) dev->Release();
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (collection != null) collection->Release();
            if (enumerator != null) enumerator->Release();
            if (didCoInit) PInvoke.CoUninitialize();
        }
        return list;
    }

    public bool Start(string? preferredDeviceId, out string statusText)
    {
        lock (_stateLock)
        {
            StopInternal();

            var devices = EnumerateRenderDevices();
            AudioDeviceInfo? chosen = null;
            if (!string.IsNullOrWhiteSpace(preferredDeviceId))
            {
                chosen = devices.FirstOrDefault(d => string.Equals(d.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
            }
            chosen ??= devices.FirstOrDefault(d => d.IsVirtualCable);
            chosen ??= devices.FirstOrDefault();

            if (chosen == null)
            {
                statusText = "No active Windows audio output device found.";
                return false;
            }

            _targetDeviceId = chosen.Id;
            _activeDeviceName = chosen.Name;
            ResetRing();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            using var initDone = new ManualResetEventSlim(false);
            string? initError = null;

            _renderThread = new Thread(() => RenderLoop(chosen.Id, chosen.Name, ct, err =>
            {
                initError = err;
                initDone.Set();
            }))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "PhoneCamWasapiAudio"
            };
            _renderThread.Start();

            initDone.Wait(2500);
            if (!string.IsNullOrEmpty(initError))
            {
                StopInternal();
                statusText = $"Audio init failed: {initError}";
                return false;
            }

            _active = true;
            statusText = chosen.IsVirtualCable
                ? $"Virtual Mic routing -> {chosen.Name}"
                : $"Audio playing -> {chosen.Name}";
            _logger.Log(statusText, force: true);
            return true;
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _active = false;
        _cts?.Cancel();
        if (_renderThread != null && _renderThread.IsAlive)
        {
            _renderThread.Join(1000);
        }
        _renderThread = null;
        _cts?.Dispose();
        _cts = null;
        _activeDeviceName = "";
        Volatile.Write(ref _currentLevel, 0.0);
        ResetRing();
    }

    private void ResetRing()
    {
        lock (_ringLock)
        {
            _ringRead = 0;
            _ringWrite = 0;
            _ringCount = 0;
            _resamplePhase = 0;
        }
    }

    public void PushPcm16Mono(ReadOnlySpan<byte> pcmBytes, int sampleRate, int channels)
    {
        if (!_active || pcmBytes.Length < 2) return;
        int strideBytes = Math.Max(1, channels) * 2;
        int frameCount = pcmBytes.Length / strideBytes;
        if (frameCount <= 0) return;

        double sumSq = 0;
        var shorts = MemoryMarshal.Cast<byte, short>(pcmBytes.Slice(0, frameCount * strideBytes));

        lock (_ringLock)
        {
            _inputSampleRate = sampleRate > 0 ? sampleRate : 48000;
            int step = Math.Max(1, channels);
            for (int i = 0; i < frameCount; i++)
            {
                short s = shorts[i * step];
                sumSq += s * (double)s;

                if (_ringCount == RingCapacity)
                {
                    _ringRead = (_ringRead + 1) % RingCapacity;
                    _ringCount--;
                }
                _ring[_ringWrite] = s;
                _ringWrite = (_ringWrite + 1) % RingCapacity;
                _ringCount++;
            }

            if (_ringCount > MaxBufferedSamples)
            {
                int drop = _ringCount - TrimTargetSamples;
                _ringRead = (_ringRead + drop) % RingCapacity;
                _ringCount -= drop;
            }
        }

        double rms = Math.Sqrt(sumSq / frameCount) / 32768.0;
        double level = Muted ? 0.0 : Math.Clamp(rms * Gain * 3.0, 0.0, 1.0);
        Volatile.Write(ref _currentLevel, level);
    }

    private void RenderLoop(string deviceId, string deviceName, CancellationToken ct, Action<string?> signalInit)
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);

        IMMDeviceEnumerator* enumerator = null;
        IMMDevice* device = null;
        IAudioClient* audioClient = null;
        IAudioRenderClient* renderClient = null;
        WAVEFORMATEX* mixFmt = null;

        try
        {
            PInvoke.CoCreateInstance(
                in ClsidMMDeviceEnumerator,
                null,
                CLSCTX.CLSCTX_ALL,
                out enumerator).ThrowOnFailure();

            fixed (char* idPtr = deviceId)
            {
                enumerator->GetDevice(idPtr, &device);
            }
            if (device == null)
            {
                signalInit("Audio device not found");
                return;
            }

            var iidAudioClient = typeof(IAudioClient).GUID;
            device->Activate(in iidAudioClient, CLSCTX.CLSCTX_ALL, null, out void* acObj);
            audioClient = (IAudioClient*)acObj;
            if (audioClient == null)
            {
                signalInit("IAudioClient Activate failed");
                return;
            }

            audioClient->GetMixFormat(&mixFmt);
            if (mixFmt == null)
            {
                signalInit("GetMixFormat failed");
                return;
            }

            // 100 ms shared-mode ring buffer
            audioClient->Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                0,
                1_000_000,
                0,
                mixFmt,
                null);

            audioClient->GetBufferSize(out uint bufferFrames);
            var iidRenderClient = typeof(IAudioRenderClient).GUID;
            audioClient->GetService(in iidRenderClient, out void* rcObj);
            renderClient = (IAudioRenderClient*)rcObj;
            if (renderClient == null)
            {
                signalInit("IAudioRenderClient GetService failed");
                return;
            }

            bool isFloat = IsFloatFormat(mixFmt);
            bool isPcm16 = IsPcm16Format(mixFmt);
            if (!isFloat && !isPcm16)
            {
                signalInit($"Unsupported WASAPI mix format (tag={mixFmt->wFormatTag}, bits={mixFmt->wBitsPerSample})");
                return;
            }

            int outRate = (int)mixFmt->nSamplesPerSec;
            int outChannels = mixFmt->nChannels;

            audioClient->Start();
            signalInit(null);

            while (!ct.IsCancellationRequested)
            {
                Thread.Sleep(8);
                if (ct.IsCancellationRequested) break;

                audioClient->GetCurrentPadding(out uint padding);
                uint available = bufferFrames - padding;
                if (available == 0) continue;

                uint framesToWrite = Math.Min(available, (uint)Math.Max(outRate / 25, 480));
                byte* dst = null;
                renderClient->GetBuffer(framesToWrite, &dst);
                if (dst == null) continue;

                uint writtenFlags = 0;
                lock (_ringLock)
                {
                    if (_ringCount < 2 || Muted)
                    {
                        if (Muted && _ringCount > 0)
                        {
                            // Keep draining ring silently so unmuting stays live
                            int drain = Math.Min(_ringCount, (int)framesToWrite);
                            _ringRead = (_ringRead + drain) % RingCapacity;
                            _ringCount -= drain;
                        }
                        writtenFlags = (uint)_AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT;
                    }
                    else
                    {
                        double ratio = (double)_inputSampleRate / outRate;
                        float gain = (float)Volatile.Read(ref _gain);

                        if (isFloat)
                        {
                            float* fOut = (float*)dst;
                            for (uint f = 0; f < framesToWrite; f++)
                            {
                                float sample = SampleLinearLocked() * gain;
                                for (int c = 0; c < outChannels; c++)
                                {
                                    *fOut++ = sample;
                                }
                                _resamplePhase += ratio;
                                int advance = (int)_resamplePhase;
                                if (advance > 0)
                                {
                                    _resamplePhase -= advance;
                                    int step = Math.Min(advance, _ringCount);
                                    _ringRead = (_ringRead + step) % RingCapacity;
                                    _ringCount -= step;
                                }
                            }
                        }
                        else
                        {
                            short* sOut = (short*)dst;
                            for (uint f = 0; f < framesToWrite; f++)
                            {
                                float val = SampleLinearLocked() * gain * 32767f;
                                short s = (short)Math.Clamp((int)val, short.MinValue, short.MaxValue);
                                for (int c = 0; c < outChannels; c++)
                                {
                                    *sOut++ = s;
                                }
                                _resamplePhase += ratio;
                                int advance = (int)_resamplePhase;
                                if (advance > 0)
                                {
                                    _resamplePhase -= advance;
                                    int step = Math.Min(advance, _ringCount);
                                    _ringRead = (_ringRead + step) % RingCapacity;
                                    _ringCount -= step;
                                }
                            }
                        }
                    }
                }

                renderClient->ReleaseBuffer(framesToWrite, writtenFlags);
            }

            audioClient->Stop();
        }
        catch (Exception ex)
        {
            _logger.Log($"WASAPI audio loop error on {deviceName}: {ex.Message}", force: true);
            signalInit(ex.Message);
        }
        finally
        {
            if (mixFmt != null) PInvoke.CoTaskMemFree(mixFmt);
            if (renderClient != null) renderClient->Release();
            if (audioClient != null) audioClient->Release();
            if (device != null) device->Release();
            if (enumerator != null) enumerator->Release();
            PInvoke.CoUninitialize();
        }
    }

    private float SampleLinearLocked()
    {
        if (_ringCount <= 0) return 0f;
        short s0 = _ring[_ringRead];
        if (_ringCount == 1) return s0 / 32768f;
        short s1 = _ring[(_ringRead + 1) % RingCapacity];
        double frac = _resamplePhase;
        return (float)((s0 + (s1 - s0) * frac) / 32768.0);
    }

    private static bool IsFloatFormat(WAVEFORMATEX* fmt)
    {
        if (fmt->wFormatTag == 3 && fmt->wBitsPerSample == 32) return true; // WAVE_FORMAT_IEEE_FLOAT
        if (fmt->wFormatTag == 0xFFFE && fmt->cbSize >= 22)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)fmt;
            return ext->SubFormat == KsDataFormatSubtypeIeeeFloat && fmt->wBitsPerSample == 32;
        }
        return false;
    }

    private static bool IsPcm16Format(WAVEFORMATEX* fmt)
    {
        if (fmt->wFormatTag == 1 && fmt->wBitsPerSample == 16) return true; // WAVE_FORMAT_PCM
        if (fmt->wFormatTag == 0xFFFE && fmt->cbSize >= 22)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)fmt;
            return ext->SubFormat == KsDataFormatSubtypePcm && fmt->wBitsPerSample == 16;
        }
        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}

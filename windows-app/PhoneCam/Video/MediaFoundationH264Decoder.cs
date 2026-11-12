using System.Runtime.InteropServices;
using PhoneCam.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace PhoneCam.Video;

public unsafe sealed class MediaFoundationH264Decoder : IDisposable
{
    private static readonly Guid ClsidMsH264DecoderMft =
        new(0x62CE7E72, 0x4C71, 0x4D20, 0xB1, 0x5D, 0x45, 0x28, 0x31, 0xA8, 0x7D, 0x9D);

    private static readonly HRESULT MfENoMoreTypes = (HRESULT)unchecked((int)0xC00D36B9);

    private readonly DebugLogger _logger;
    private readonly object _decodeLock = new();

    private bool _mfStarted;
    private IMFTransform* _mft;
    private IMFDXGIDeviceManager* _dxgiManager;
    private ID3D11Device* _d3dDevice;
    private ID3D11DeviceContext* _d3dContext;
    private ID3D11VideoDevice* _videoDevice;
    private ID3D11VideoContext* _videoContext;
    private ID3D11VideoProcessorEnumerator* _vpEnum;
    private ID3D11VideoProcessor* _vp;
    private ID3D11Texture2D* _bgraRtTex;
    private ID3D11Texture2D* _bgraStagingTex;
    private ID3D11VideoProcessorOutputView* _vpOutView;
    private int _vpWidth;
    private int _vpHeight;
    private int _appliedVpRotation = -1;

    private ID3D11Texture2D* _nv12StagingTex;
    private int _nv12StagingWidth;
    private int _nv12StagingHeight;
    private byte[] _nv12RawScratch = Array.Empty<byte>();

    private IMFSample* _swOutputSample;
    private IMFMediaBuffer* _swOutputBuffer;
    private bool _mftProvidesOutputSamples;

    private byte[]? _codecConfig;
    private bool _needCodecConfigPrepend = true;
    private bool _hardwareDecode;
    private bool _disposed;
    private int _rotationQuarters;
    private bool _mirrorH;
    private bool _flipV;

    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsInitialized => _mft != null && !_disposed;
    public bool IsHardwareDecode => _hardwareDecode;

    public MediaFoundationH264Decoder(DebugLogger logger)
    {
        _logger = logger;
    }

    public void SetTransform(int rotationQuarters, bool mirrorH, bool flipV)
    {
        Volatile.Write(ref _rotationQuarters, ((rotationQuarters % 4) + 4) % 4);
        Volatile.Write(ref _mirrorH, mirrorH);
        Volatile.Write(ref _flipV, flipV);
    }

    public void Initialize(int width, int height, int fps)
    {
        lock (_decodeLock)
        {
            CleanupInternal();

            InputWidth = width;
            InputHeight = height;
            Width = width;
            Height = height;
            _needCodecConfigPrepend = true;

            PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
            _mfStarted = true;

            IMFTransform* mft = null;
            PInvoke.CoCreateInstance(
                in ClsidMsH264DecoderMft,
                null,
                CLSCTX.CLSCTX_INPROC_SERVER,
                out mft).ThrowOnFailure();
            _mft = mft;

            IMFAttributes* attrs = null;
            _mft->GetAttributes(&attrs);
            if (attrs != null)
            {
                attrs->SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
                attrs->Release();
            }

            TrySetupD3D11();

            SetInputType(width, height, fps);
            SelectNv12OutputType();

            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

            MFT_OUTPUT_STREAM_INFO outInfo = default;
            _mft->GetOutputStreamInfo(0, &outInfo);
            const uint provideFlags =
                (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES |
                (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES;
            _mftProvidesOutputSamples = (outInfo.dwFlags & provideFlags) != 0;

            _logger.Log(
                $"H.264 decoder ready ({width}x{height}@{fps}, hw={_hardwareDecode}, mftSamples={_mftProvidesOutputSamples})",
                force: true);
        }
    }

    private void TrySetupD3D11()
    {
        try
        {
            ID3D11Device* dev = null;
            ID3D11DeviceContext* ctx = null;
            D3D_FEATURE_LEVEL[] levels =
            [
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
            ];

            var hr = PInvoke.D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT |
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels,
                PInvoke.D3D11_SDK_VERSION,
                &dev,
                out _,
                &ctx);

            if (hr.Failed || dev == null || ctx == null)
            {
                _logger.Log($"D3D11 device creation failed (0x{hr.Value:X8}); using software decode", force: true);
                return;
            }

            _d3dDevice = dev;
            _d3dContext = ctx;

            if (dev->QueryInterface(out ID3D10Multithread* mt).Succeeded && mt != null)
            {
                mt->SetMultithreadProtected(true);
                mt->Release();
            }

            if (dev->QueryInterface(out ID3D11VideoDevice* vdev).Failed || vdev == null ||
                ctx->QueryInterface(out ID3D11VideoContext* vctx).Failed || vctx == null)
            {
                if (vdev != null) vdev->Release();
                _logger.Log("D3D11 VideoDevice/VideoContext unavailable; using software decode", force: true);
                return;
            }
            _videoDevice = vdev;
            _videoContext = vctx;

            IMFDXGIDeviceManager* mgr = null;
            PInvoke.MFCreateDXGIDeviceManager(out uint resetToken, &mgr).ThrowOnFailure();
            _dxgiManager = mgr;
            _dxgiManager->ResetDevice((IUnknown*)dev, resetToken);

            _mft->ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)_dxgiManager);
            _hardwareDecode = true;
        }
        catch (Exception ex)
        {
            _hardwareDecode = false;
            _logger.Log($"D3D11 hardware setup fallback: {ex.Message}", force: true);
        }
    }

    private void SetInputType(int width, int height, int fps)
    {
        IMFMediaType* inType = null;
        PInvoke.MFCreateMediaType(&inType).ThrowOnFailure();
        try
        {
            inType->SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            inType->SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            inType->SetUINT64(PInvoke.MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
            inType->SetUINT64(PInvoke.MF_MT_FRAME_RATE, ((ulong)(uint)fps << 32) | 1u);
            inType->SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            _mft->SetInputType(0, inType, 0);
        }
        finally
        {
            if (inType != null) inType->Release();
        }
    }

    private void SelectNv12OutputType()
    {
        var vtbl = *(IMFTransform.Vtbl**)_mft;
        for (uint idx = 0; ; idx++)
        {
            IMFMediaType* outType = null;
            HRESULT hr = vtbl->GetOutputAvailableType_15(_mft, 0, idx, &outType);
            if (hr == MfENoMoreTypes || hr.Failed || outType == null)
            {
                break;
            }

            try
            {
                outType->GetGUID(PInvoke.MF_MT_SUBTYPE, out Guid subtype);
                if (subtype == PInvoke.MFVideoFormat_NV12)
                {
                    try
                    {
                        outType->GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out ulong frameSize);
                        if (frameSize != 0)
                        {
                            int w = (int)(frameSize >> 32);
                            int h = (int)(frameSize & 0xFFFFFFFF);
                            if (w > 0 && h > 0)
                            {
                                Width = w;
                                Height = h;
                            }
                        }
                    }
                    catch
                    {
                    }
                    _mft->SetOutputType(0, outType, 0);
                    return;
                }
            }
            finally
            {
                outType->Release();
            }
        }
        throw new InvalidOperationException("No NV12 output media type offered by H.264 MFT.");
    }

    public void SetCodecConfig(ReadOnlySpan<byte> spsPps)
    {
        lock (_decodeLock)
        {
            _codecConfig = spsPps.ToArray();
            _needCodecConfigPrepend = true;
        }
    }

    public bool DecodeFrame(
        ReadOnlySpan<byte> annexBPayload,
        bool isKeyframe,
        Func<int, int, byte[]> ensureBgraBuffer,
        Func<int, int, byte[]>? ensureNv12Buffer,
        out int decodedWidth,
        out int decodedHeight)
    {
        lock (_decodeLock)
        {
            decodedWidth = Width;
            decodedHeight = Height;
            if (_disposed || _mft == null) return false;

            bool prependConfig = (_needCodecConfigPrepend || isKeyframe) && _codecConfig is { Length: > 0 };
            int totalBytes = annexBPayload.Length + (prependConfig ? _codecConfig!.Length : 0);

            IMFSample* inSample = null;
            IMFMediaBuffer* inBuffer = null;
            PInvoke.MFCreateSample(&inSample).ThrowOnFailure();
            PInvoke.MFCreateMemoryBuffer((uint)totalBytes, &inBuffer).ThrowOnFailure();

            try
            {
                byte* dst = null;
                inBuffer->Lock(&dst, null, null);
                try
                {
                    int offset = 0;
                    if (prependConfig && _codecConfig != null)
                    {
                        _codecConfig.AsSpan().CopyTo(new Span<byte>(dst, _codecConfig.Length));
                        offset = _codecConfig.Length;
                        _needCodecConfigPrepend = false;
                    }
                    annexBPayload.CopyTo(new Span<byte>(dst + offset, annexBPayload.Length));
                }
                finally
                {
                    inBuffer->Unlock();
                }

                inBuffer->SetCurrentLength((uint)totalBytes);
                inSample->AddBuffer(inBuffer);

                var vtbl = *(IMFTransform.Vtbl**)_mft;
                HRESULT inHr = vtbl->ProcessInput_25(_mft, 0, inSample, 0);
                if (inHr.Failed)
                {
                    _logger.Log($"ProcessInput failed: 0x{inHr.Value:X8}");
                    return false;
                }
            }
            finally
            {
                inBuffer->Release();
                inSample->Release();
            }

            return DrainOutputs(ensureBgraBuffer, ensureNv12Buffer, out decodedWidth, out decodedHeight);
        }
    }

    private bool DrainOutputs(
        Func<int, int, byte[]> ensureBgraBuffer,
        Func<int, int, byte[]>? ensureNv12Buffer,
        out int decodedWidth,
        out int decodedHeight)
    {
        decodedWidth = Width;
        decodedHeight = Height;
        bool producedFrame = false;
        var vtbl = *(IMFTransform.Vtbl**)_mft;

        while (true)
        {
            MFT_OUTPUT_DATA_BUFFER outBuf = default;
            if (!_mftProvidesOutputSamples)
            {
                EnsureSoftwareOutputSample();
                outBuf.pSample = _swOutputSample;
            }

            uint status = 0;
            HRESULT hr = vtbl->ProcessOutput_26(_mft, 0, 1, &outBuf, &status);

            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (outBuf.pEvents != null) outBuf.pEvents->Release();
                SelectNv12OutputType();
                ReleaseSoftwareOutputSample();
                decodedWidth = Width;
                decodedHeight = Height;
                continue;
            }

            if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                if (outBuf.pEvents != null) outBuf.pEvents->Release();
                break;
            }

            if (hr.Failed)
            {
                if (outBuf.pEvents != null) outBuf.pEvents->Release();
                _logger.Log($"ProcessOutput error: 0x{hr.Value:X8}");
                break;
            }

            try
            {
                if (outBuf.pSample != null)
                {
                    byte[] dstBgra = ensureBgraBuffer(Width, Height);
                    byte[]? dstNv12 = ensureNv12Buffer?.Invoke(Width, Height);
                    int rot = Volatile.Read(ref _rotationQuarters);
                    bool mirrorH = Volatile.Read(ref _mirrorH);
                    bool flipV = Volatile.Read(ref _flipV);
                    if (CopySampleToBuffers(outBuf.pSample, dstBgra, dstNv12, Width, Height, rot, mirrorH, flipV))
                    {
                        decodedWidth = Width;
                        decodedHeight = Height;
                        producedFrame = true;
                    }
                }
            }
            finally
            {
                if (_mftProvidesOutputSamples && outBuf.pSample != null)
                {
                    outBuf.pSample->Release();
                }
                if (outBuf.pEvents != null)
                {
                    outBuf.pEvents->Release();
                }
            }
        }

        return producedFrame;
    }

    private void EnsureSoftwareOutputSample()
    {
        if (_swOutputSample != null) return;
        MFT_OUTPUT_STREAM_INFO info = default;
        _mft->GetOutputStreamInfo(0, &info);
        uint size = Math.Max(info.cbSize, (uint)(Width * Height * 3 / 2));
        IMFSample* sample = null;
        IMFMediaBuffer* buf = null;
        PInvoke.MFCreateSample(&sample).ThrowOnFailure();
        PInvoke.MFCreateMemoryBuffer(size, &buf).ThrowOnFailure();
        sample->AddBuffer(buf);
        _swOutputSample = sample;
        _swOutputBuffer = buf;
    }

    private void ReleaseSoftwareOutputSample()
    {
        if (_swOutputBuffer != null) { _swOutputBuffer->Release(); _swOutputBuffer = null; }
        if (_swOutputSample != null) { _swOutputSample->Release(); _swOutputSample = null; }
    }

    private bool CopySampleToBuffers(
        IMFSample* sample,
        byte[] dstBgra,
        byte[]? dstNv12,
        int width,
        int height,
        int rotQuarters,
        bool mirrorH,
        bool flipV)
    {
        IMFMediaBuffer* buffer = null;
        sample->GetBufferByIndex(0, &buffer);
        if (buffer == null) return false;

        try
        {
            if (_videoDevice != null && _videoContext != null &&
                buffer->QueryInterface(out IMFDXGIBuffer* dxgiBuf).Succeeded && dxgiBuf != null)
            {
                try
                {
                    Guid texGuid = ID3D11Texture2D.IID_Guid;
                    dxgiBuf->GetResource(in texGuid, out void* srcTexVoid);
                    if (srcTexVoid != null)
                    {
                        var srcTex = (ID3D11Texture2D*)srcTexVoid;
                        try
                        {
                            dxgiBuf->GetSubresourceIndex(out uint subresourceIndex);
                            if (dstNv12 != null)
                            {
                                CopyD3D11Nv12ToCpuTransformed(
                                    srcTex, subresourceIndex, dstNv12, width, height, rotQuarters, mirrorH, flipV);
                            }
                            return ConvertD3D11Nv12ToBgra(
                                srcTex, subresourceIndex, dstBgra, width, height, rotQuarters, mirrorH, flipV);
                        }
                        finally
                        {
                            srcTex->Release();
                        }
                    }
                }
                finally
                {
                    dxgiBuf->Release();
                }
            }

            if (buffer->QueryInterface(out IMF2DBuffer* buf2d).Succeeded && buf2d != null)
            {
                try
                {
                    byte* scan0 = null;
                    int pitch = 0;
                    buf2d->Lock2D(&scan0, &pitch);
                    try
                    {
                        if (dstNv12 != null)
                            TransformNv12FromRaw(scan0, pitch, dstNv12, width, height, rotQuarters, mirrorH, flipV);
                        Nv12ToBgraCpu(scan0, pitch, dstBgra, width, height, rotQuarters, mirrorH, flipV);
                        return true;
                    }
                    finally
                    {
                        buf2d->Unlock2D();
                    }
                }
                finally
                {
                    buf2d->Release();
                }
            }

            byte* rawPtr = null;
            uint curLen = 0;
            buffer->Lock(&rawPtr, null, &curLen);
            try
            {
                if (curLen >= (uint)(width * height * 3 / 2))
                {
                    if (dstNv12 != null)
                        TransformNv12FromRaw(rawPtr, width, dstNv12, width, height, rotQuarters, mirrorH, flipV);
                    Nv12ToBgraCpu(rawPtr, width, dstBgra, width, height, rotQuarters, mirrorH, flipV);
                    return true;
                }
            }
            finally
            {
                buffer->Unlock();
            }
            return false;
        }
        finally
        {
            buffer->Release();
        }
    }

    private void CopyD3D11Nv12ToCpuTransformed(
        ID3D11Texture2D* srcTex,
        uint subresourceIndex,
        byte[] dstNv12,
        int width,
        int height,
        int rotQuarters,
        bool mirrorH,
        bool flipV)
    {
        EnsureNv12Staging(width, height);
        if (_nv12StagingTex == null) return;

        _d3dContext->CopySubresourceRegion(
            (ID3D11Resource*)_nv12StagingTex, 0, 0, 0, 0,
            (ID3D11Resource*)srcTex, subresourceIndex, null);

        D3D11_MAPPED_SUBRESOURCE mapped = default;
        _d3dContext->Map((ID3D11Resource*)_nv12StagingTex, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            TransformNv12FromRaw(
                (byte*)mapped.pData, (int)mapped.RowPitch, dstNv12, width, height, rotQuarters, mirrorH, flipV);
        }
        finally
        {
            _d3dContext->Unmap((ID3D11Resource*)_nv12StagingTex, 0);
        }
    }

    private void EnsureNv12Staging(int width, int height)
    {
        if (_nv12StagingTex != null && _nv12StagingWidth == width && _nv12StagingHeight == height) return;

        if (_nv12StagingTex != null) { _nv12StagingTex->Release(); _nv12StagingTex = null; }
        _nv12StagingWidth = width;
        _nv12StagingHeight = height;

        D3D11_TEXTURE2D_DESC desc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ
        };

        ID3D11Texture2D* tex = null;
        _d3dDevice->CreateTexture2D(&desc, null, &tex);
        _nv12StagingTex = tex;
    }

    private void TransformNv12FromRaw(
        byte* src,
        int pitch,
        byte[] dstNv12,
        int width,
        int height,
        int rotQuarters,
        bool mirrorH,
        bool flipV)
    {
        if (rotQuarters == 0 && !mirrorH && !flipV)
        {
            CopyTightNv12(src, pitch, dstNv12, width, height);
            return;
        }

        byte* tightSrc = src;
        int tightSize = width * height * 3 / 2;
        if (pitch != width)
        {
            if (_nv12RawScratch.Length < tightSize) _nv12RawScratch = new byte[tightSize];
            CopyTightNv12(src, pitch, _nv12RawScratch, width, height);
        }

        fixed (byte* scratchPtr = _nv12RawScratch)
        fixed (byte* dstBase = dstNv12)
        {
            if (pitch != width) tightSrc = scratchPtr;

            byte* srcY = tightSrc;
            byte* srcUV = tightSrc + width * height;
            byte* dstY = dstBase;
            byte* dstUV = dstBase + width * height;

            if (rotQuarters is 0 or 2)
            {
                bool fx = (rotQuarters == 2) ^ mirrorH;
                bool fy = (rotQuarters == 2) ^ flipV;
                if (!fx && !fy)
                {
                    Buffer.MemoryCopy(tightSrc, dstBase, dstNv12.Length, tightSize);
                    return;
                }

                for (int y = 0; y < height; y++)
                {
                    int sy = fy ? height - 1 - y : y;
                    byte* sRow = srcY + sy * width;
                    byte* dRow = dstY + y * width;
                    if (!fx)
                    {
                        Buffer.MemoryCopy(sRow, dRow, width, width);
                    }
                    else
                    {
                        for (int x = 0; x < width; x++) dRow[x] = sRow[width - 1 - x];
                    }
                }

                int h2 = height / 2;
                int w2 = width / 2;
                for (int y = 0; y < h2; y++)
                {
                    int sy = fy ? h2 - 1 - y : y;
                    byte* sRow = srcUV + sy * width;
                    byte* dRow = dstUV + y * width;
                    if (!fx)
                    {
                        Buffer.MemoryCopy(sRow, dRow, width, width);
                    }
                    else
                    {
                        for (int x = 0; x < w2; x++)
                        {
                            int sx = (w2 - 1 - x) * 2;
                            int dx = x * 2;
                            dRow[dx] = sRow[sx];
                            dRow[dx + 1] = sRow[sx + 1];
                        }
                    }
                }
                return;
            }

            // 90° or 270° portrait pillarboxed inside width x height (even alignment for NV12 2x2 chroma).
            new Span<byte>(dstY, width * height).Fill(16);
            new Span<byte>(dstUV, (width * height) / 2).Fill(128);

            int fitH = height & ~1;
            int fitW = ((height * height) / width) & ~1;
            if (fitW < 2) fitW = 2;
            if (fitW > width) fitW = width & ~1;
            int offX = ((width - fitW) / 2) & ~1;

            bool cw90 = rotQuarters == 1;
            for (int py = 0; py < fitH; py++)
            {
                int sx = cw90 ? (py * width) / fitH : width - 1 - (py * width) / fitH;
                int outY = flipV ? fitH - 1 - py : py;
                byte* dRow = dstY + outY * width + offX;
                for (int px = 0; px < fitW; px++)
                {
                    int sy = cw90 ? height - 1 - (px * height) / fitW : (px * height) / fitW;
                    int outX = mirrorH ? fitW - 1 - px : px;
                    dRow[outX] = srcY[sy * width + sx];
                }
            }

            int fitH2 = fitH / 2;
            int fitW2 = fitW / 2;
            int offX2 = offX / 2;
            int srcH2 = height / 2;
            int srcW2 = width / 2;
            for (int py = 0; py < fitH2; py++)
            {
                int sx2 = cw90 ? (py * srcW2) / fitH2 : srcW2 - 1 - (py * srcW2) / fitH2;
                int outY = flipV ? fitH2 - 1 - py : py;
                byte* dRow = dstUV + outY * width + offX2 * 2;
                for (int px = 0; px < fitW2; px++)
                {
                    int sy2 = cw90 ? srcH2 - 1 - (px * srcH2) / fitW2 : (px * srcH2) / fitW2;
                    int sIdx = sy2 * width + sx2 * 2;
                    int dIdx = (mirrorH ? fitW2 - 1 - px : px) * 2;
                    dRow[dIdx] = srcUV[sIdx];
                    dRow[dIdx + 1] = srcUV[sIdx + 1];
                }
            }
        }
    }

    private static void CopyTightNv12(byte* src, int pitch, byte[] dstNv12, int width, int height)
    {
        fixed (byte* dstBase = dstNv12)
        {
            int totalRows = height + height / 2;
            if (pitch == width)
            {
                long bytes = (long)width * totalRows;
                Buffer.MemoryCopy(src, dstBase, dstNv12.Length, bytes);
            }
            else
            {
                for (int y = 0; y < totalRows; y++)
                {
                    Buffer.MemoryCopy(src + (long)y * pitch, dstBase + (long)y * width, width, width);
                }
            }
        }
    }

    private bool ConvertD3D11Nv12ToBgra(
        ID3D11Texture2D* srcTex,
        uint subresourceIndex,
        byte[] dstBgra,
        int width,
        int height,
        int rotQuarters,
        bool mirrorH,
        bool flipV)
    {
        EnsureVideoProcessor(width, height, rotQuarters);
        if (_vp == null || _vpEnum == null || _vpOutView == null || _bgraStagingTex == null)
        {
            return false;
        }

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inDesc = new()
        {
            FourCC = 0,
            ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
            Anonymous = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC._Anonymous_e__Union
            {
                Texture2D = new D3D11_TEX2D_VPIV
                {
                    MipSlice = 0,
                    ArraySlice = subresourceIndex
                }
            }
        };

        ID3D11VideoProcessorInputView* inView = null;
        _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)srcTex, _vpEnum, &inDesc, &inView);
        if (inView == null) return false;

        try
        {
            D3D11_VIDEO_PROCESSOR_STREAM stream = new()
            {
                Enable = true,
                pInputSurface = inView
            };

            _videoContext->VideoProcessorBlt(_vp, _vpOutView, 0, 1, &stream);
            _d3dContext->CopyResource((ID3D11Resource*)_bgraStagingTex, (ID3D11Resource*)_bgraRtTex);

            D3D11_MAPPED_SUBRESOURCE mapped = default;
            _d3dContext->Map((ID3D11Resource*)_bgraStagingTex, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                int rowBytes = width * 4;
                byte* src = (byte*)mapped.pData;
                fixed (byte* dstBase = dstBgra)
                {
                    if (!mirrorH && !flipV)
                    {
                        if ((int)mapped.RowPitch == rowBytes)
                        {
                            Buffer.MemoryCopy(src, dstBase, dstBgra.Length, (long)rowBytes * height);
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + (long)y * mapped.RowPitch,
                                    dstBase + (long)y * rowBytes,
                                    rowBytes,
                                    rowBytes);
                            }
                        }
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                        {
                            int sy = flipV ? height - 1 - y : y;
                            uint* sRow = (uint*)(src + (long)sy * mapped.RowPitch);
                            uint* dRow = (uint*)(dstBase + (long)y * rowBytes);
                            if (!mirrorH)
                            {
                                Buffer.MemoryCopy(sRow, dRow, rowBytes, rowBytes);
                            }
                            else
                            {
                                for (int x = 0; x < width; x++)
                                {
                                    dRow[width - 1 - x] = sRow[x];
                                }
                            }
                        }
                    }
                }
                return true;
            }
            finally
            {
                _d3dContext->Unmap((ID3D11Resource*)_bgraStagingTex, 0);
            }
        }
        finally
        {
            inView->Release();
        }
    }

    private void EnsureVideoProcessor(int width, int height, int rotQuarters)
    {
        if (_vp == null || _vpWidth != width || _vpHeight != height)
        {
            ReleaseVideoProcessorResources();
            _vpWidth = width;
            _vpHeight = height;
            _appliedVpRotation = -1;

            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc = new()
            {
                InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL
            };

            ID3D11VideoProcessorEnumerator* vpEnum = null;
            _videoDevice->CreateVideoProcessorEnumerator(&desc, &vpEnum);
            _vpEnum = vpEnum;

            ID3D11VideoProcessor* vp = null;
            _videoDevice->CreateVideoProcessor(vpEnum, 0, &vp);
            _vp = vp;

            D3D11_VIDEO_COLOR bgColor = default;
            bgColor.Anonymous.RGBA = new D3D11_VIDEO_COLOR_RGBA { R = 0f, G = 0f, B = 0f, A = 1f };
            _videoContext->VideoProcessorSetOutputBackgroundColor(_vp, false, &bgColor);

            D3D11_TEXTURE2D_DESC rtDesc = new()
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
            };
            ID3D11Texture2D* rtTex = null;
            _d3dDevice->CreateTexture2D(&rtDesc, null, &rtTex);
            _bgraRtTex = rtTex;

            D3D11_TEXTURE2D_DESC stDesc = rtDesc;
            stDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            stDesc.BindFlags = 0;
            stDesc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            ID3D11Texture2D* stTex = null;
            _d3dDevice->CreateTexture2D(&stDesc, null, &stTex);
            _bgraStagingTex = stTex;

            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outDesc = new()
            {
                ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D
            };
            ID3D11VideoProcessorOutputView* outView = null;
            _videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)rtTex, vpEnum, &outDesc, &outView);
            _vpOutView = outView;
        }

        if (_appliedVpRotation != rotQuarters && _vp != null)
        {
            _appliedVpRotation = rotQuarters;
            var d3dRot = rotQuarters switch
            {
                1 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_90,
                2 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_180,
                3 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_270,
                _ => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY
            };

            _videoContext->VideoProcessorSetStreamRotation(_vp, 0, rotQuarters != 0, d3dRot);

            RECT destRect;
            if (rotQuarters is 1 or 3)
            {
                int fitH = height & ~1;
                int fitW = ((height * height) / width) & ~1;
                int left = (width - fitW) / 2;
                destRect = new RECT { left = left, top = 0, right = left + fitW, bottom = fitH };
            }
            else
            {
                destRect = new RECT { left = 0, top = 0, right = width, bottom = height };
            }
            _videoContext->VideoProcessorSetStreamDestRect(_vp, 0, true, &destRect);
        }
    }

    private static void Nv12ToBgraCpu(
        byte* nv12,
        int pitch,
        byte[] dstBgra,
        int width,
        int height,
        int rotQuarters,
        bool mirrorH,
        bool flipV)
    {
        byte* yPlane = nv12;
        byte* uvPlane = nv12 + pitch * height;

        if (rotQuarters is 1 or 3)
        {
            Array.Clear(dstBgra);
            int fitH = height & ~1;
            int fitW = ((height * height) / width) & ~1;
            int offX = (width - fitW) / 2;
            bool cw90 = rotQuarters == 1;

            fixed (byte* dstBase = dstBgra)
            {
                for (int py = 0; py < fitH; py++)
                {
                    int sx = cw90 ? (py * width) / fitH : width - 1 - (py * width) / fitH;
                    int outY = flipV ? fitH - 1 - py : py;
                    byte* outRow = dstBase + (outY * width + offX) * 4;
                    for (int px = 0; px < fitW; px++)
                    {
                        int sy = cw90 ? height - 1 - (px * height) / fitW : (px * height) / fitW;
                        int c = yPlane[sy * pitch + sx] - 16;
                        int uvIdx = sx & ~1;
                        byte* uvRow = uvPlane + (sy >> 1) * pitch;
                        int d = uvRow[uvIdx] - 128;
                        int e = uvRow[uvIdx + 1] - 128;

                        int y298 = 298 * c;
                        int r = (y298 + 409 * e + 128) >> 8;
                        int g = (y298 - 100 * d - 208 * e + 128) >> 8;
                        int b = (y298 + 516 * d + 128) >> 8;

                        int o = (mirrorH ? fitW - 1 - px : px) * 4;
                        outRow[o] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                        outRow[o + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                        outRow[o + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                        outRow[o + 3] = 255;
                    }
                }
            }
            return;
        }

        bool fx = (rotQuarters == 2) ^ mirrorH;
        bool fy = (rotQuarters == 2) ^ flipV;

        fixed (byte* dstBase = dstBgra)
        {
            for (int y = 0; y < height; y++)
            {
                int sy = fy ? height - 1 - y : y;
                byte* yRow = yPlane + sy * pitch;
                byte* uvRow = uvPlane + (sy >> 1) * pitch;
                byte* outRow = dstBase + y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    int sx = fx ? width - 1 - x : x;
                    int c = yRow[sx] - 16;
                    int uvIdx = sx & ~1;
                    int d = uvRow[uvIdx] - 128;
                    int e = uvRow[uvIdx + 1] - 128;

                    int y298 = 298 * c;
                    int r = (y298 + 409 * e + 128) >> 8;
                    int g = (y298 - 100 * d - 208 * e + 128) >> 8;
                    int b = (y298 + 516 * d + 128) >> 8;

                    int px = x * 4;
                    outRow[px] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : b));
                    outRow[px + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : g));
                    outRow[px + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : r));
                    outRow[px + 3] = 255;
                }
            }
        }
    }

    private void ReleaseVideoProcessorResources()
    {
        if (_vpOutView != null) { _vpOutView->Release(); _vpOutView = null; }
        if (_bgraStagingTex != null) { _bgraStagingTex->Release(); _bgraStagingTex = null; }
        if (_bgraRtTex != null) { _bgraRtTex->Release(); _bgraRtTex = null; }
        if (_vp != null) { _vp->Release(); _vp = null; }
        if (_vpEnum != null) { _vpEnum->Release(); _vpEnum = null; }
        _appliedVpRotation = -1;
    }

    private void CleanupInternal()
    {
        ReleaseSoftwareOutputSample();
        ReleaseVideoProcessorResources();

        if (_nv12StagingTex != null) { _nv12StagingTex->Release(); _nv12StagingTex = null; }
        if (_mft != null) { _mft->Release(); _mft = null; }
        if (_dxgiManager != null) { _dxgiManager->Release(); _dxgiManager = null; }
        if (_videoContext != null) { _videoContext->Release(); _videoContext = null; }
        if (_videoDevice != null) { _videoDevice->Release(); _videoDevice = null; }
        if (_d3dContext != null) { _d3dContext->Release(); _d3dContext = null; }
        if (_d3dDevice != null) { _d3dDevice->Release(); _d3dDevice = null; }

        if (_mfStarted)
        {
            PInvoke.MFShutdown();
            _mfStarted = false;
        }
        _hardwareDecode = false;
    }

    public void Dispose()
    {
        lock (_decodeLock)
        {
            if (_disposed) return;
            _disposed = true;
            CleanupInternal();
        }
    }
}

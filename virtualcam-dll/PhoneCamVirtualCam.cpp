#include "PhoneCamVirtualCam.h"

#include <new>
#include <cstring>

// FB6C4281-0353-11d1-905F-0000C0CC16BA (PINNAME_VIDEO_CAPTURE from ksmedia.h)
static const GUID kPinNameVideoCapture = {
    0xfb6c4281, 0x0353, 0x11d1, { 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba }
};

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Make;

namespace {

std::atomic<long> g_dllRefCount{ 0 };
HMODULE g_hModule = nullptr;

HRESULT CreateNv12MediaType(uint32_t width, uint32_t height, uint32_t fps, IMFMediaType** ppType) {
    ComPtr<IMFMediaType> type;
    HRESULT hr = MFCreateMediaType(&type);
    if (FAILED(hr)) return hr;

    const uint32_t sampleSize = width * height * 3 / 2;
    if (FAILED(hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video))) return hr;
    if (FAILED(hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12))) return hr;
    if (FAILED(hr = type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive))) return hr;
    if (FAILED(hr = type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE))) return hr;
    if (FAILED(hr = type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE))) return hr;
    if (FAILED(hr = type->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize))) return hr;
    if (FAILED(hr = type->SetUINT32(MF_MT_DEFAULT_STRIDE, width))) return hr;
    if (FAILED(hr = MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height))) return hr;
    if (FAILED(hr = MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, fps, 1))) return hr;
    if (FAILED(hr = MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1))) return hr;

    *ppType = type.Detach();
    return S_OK;
}

void CopyResizedNv12(
    const uint8_t* src, uint32_t srcW, uint32_t srcH,
    uint8_t* dst, uint32_t dstW, uint32_t dstH)
{
    if (srcW == dstW && srcH == dstH) {
        std::memcpy(dst, src, static_cast<size_t>(dstW) * dstH * 3 / 2);
        return;
    }

    const uint8_t* srcY = src;
    const uint8_t* srcUV = src + static_cast<size_t>(srcW) * srcH;
    uint8_t* dstY = dst;
    uint8_t* dstUV = dst + static_cast<size_t>(dstW) * dstH;

    for (uint32_t y = 0; y < dstH; ++y) {
        const uint32_t sy = (y * srcH) / dstH;
        const uint8_t* sRow = srcY + static_cast<size_t>(sy) * srcW;
        uint8_t* dRow = dstY + static_cast<size_t>(y) * dstW;
        for (uint32_t x = 0; x < dstW; ++x) {
            dRow[x] = sRow[(x * srcW) / dstW];
        }
    }

    const uint32_t dstHalfH = dstH / 2;
    const uint32_t dstHalfW = dstW / 2;
    const uint32_t srcHalfW = srcW / 2;
    for (uint32_t y = 0; y < dstHalfH; ++y) {
        const uint32_t sy = (y * (srcH / 2)) / dstHalfH;
        const uint8_t* sRow = srcUV + static_cast<size_t>(sy) * srcW;
        uint8_t* dRow = dstUV + static_cast<size_t>(y) * dstW;
        for (uint32_t x = 0; x < dstHalfW; ++x) {
            const uint32_t sx = (x * srcHalfW) / dstHalfW;
            dRow[x * 2] = sRow[sx * 2];
            dRow[x * 2 + 1] = sRow[sx * 2 + 1];
        }
    }
}

} // namespace

// ============================================================================
// PhoneCamMediaStream
// ============================================================================

PhoneCamMediaStream::PhoneCamMediaStream() {
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

PhoneCamMediaStream::~PhoneCamMediaStream() {
    CloseSharedMemory();
    g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
}

HRESULT PhoneCamMediaStream::Initialize(PhoneCamMediaSource* parentSource) {
    if (!parentSource) return E_POINTER;

    HRESULT hr = MFCreateEventQueue(&m_eventQueue);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaType> type720;
    ComPtr<IMFMediaType> type1080;
    if (FAILED(hr = CreateNv12MediaType(1280, 720, 30, &type720))) return hr;
    if (FAILED(hr = CreateNv12MediaType(1920, 1080, 30, &type1080))) return hr;

    IMFMediaType* types[2] = { type720.Get(), type1080.Get() };
    if (FAILED(hr = MFCreateStreamDescriptor(0, 2, types, &m_streamDescriptor))) return hr;

    m_streamDescriptor->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, kPinNameVideoCapture);
    m_streamDescriptor->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    m_streamDescriptor->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    m_streamDescriptor->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);

    ComPtr<IMFMediaTypeHandler> handler;
    if (FAILED(hr = m_streamDescriptor->GetMediaTypeHandler(&handler))) return hr;
    if (FAILED(hr = handler->SetCurrentMediaType(type720.Get()))) return hr;

    m_parentRaw = parentSource;
    m_parentRef = parentSource;
    return S_OK;
}

HRESULT PhoneCamMediaStream::Start(const PROPVARIANT* pvarStartPosition) {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;

    ComPtr<IMFMediaTypeHandler> handler;
    if (SUCCEEDED(m_streamDescriptor->GetMediaTypeHandler(&handler))) {
        ComPtr<IMFMediaType> curType;
        if (SUCCEEDED(handler->GetCurrentMediaType(&curType))) {
            UINT32 w = 1280, h = 720;
            if (SUCCEEDED(MFGetAttributeSize(curType.Get(), MF_MT_FRAME_SIZE, &w, &h)) && w > 0 && h > 0) {
                m_width = w;
                m_height = h;
            }
            UINT32 num = 30, den = 1;
            if (SUCCEEDED(MFGetAttributeRatio(curType.Get(), MF_MT_FRAME_RATE, &num, &den)) && den > 0 && num > 0) {
                m_fps = num / den;
                if (m_fps == 0) m_fps = 30;
            }
        }
    }

    m_active = true;
    if (!m_workerRunning) {
        m_workerRunning = true;
        m_worker = std::thread(&PhoneCamMediaStream::DeliveryWorkerLoop, this);
    }

    PROPVARIANT emptyVar;
    PropVariantInit(&emptyVar);
    const PROPVARIANT* pv = pvarStartPosition ? pvarStartPosition : &emptyVar;
    return m_eventQueue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, pv);
}

HRESULT PhoneCamMediaStream::Stop() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;

    m_active = false;
    m_pendingTokens.clear();
    m_cv.notify_all();
    return m_eventQueue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
}

HRESULT PhoneCamMediaStream::Pause() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;

    m_active = false;
    m_cv.notify_all();
    return m_eventQueue->QueueEventParamVar(MEStreamPaused, GUID_NULL, S_OK, nullptr);
}

HRESULT PhoneCamMediaStream::Shutdown() {
    std::thread workerToJoin;
    {
        std::unique_lock<std::mutex> lock(m_lock);
        if (m_shutdown) return S_OK;
        m_shutdown = true;
        m_active = false;
        m_pendingTokens.clear();
        m_cv.notify_all();
        if (m_worker.joinable()) {
            workerToJoin = std::move(m_worker);
        }
        m_workerRunning = false;
    }

    if (workerToJoin.joinable()) {
        workerToJoin.join();
    }

    CloseSharedMemory();
    if (m_eventQueue) {
        m_eventQueue->Shutdown();
    }
    m_parentRaw = nullptr;
    m_parentRef.Reset();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) {
    return m_eventQueue ? m_eventQueue->GetEvent(dwFlags, ppEvent) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) {
    return m_eventQueue ? m_eventQueue->BeginGetEvent(pCallback, punkState) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) {
    return m_eventQueue ? m_eventQueue->EndGetEvent(pResult, ppEvent) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaStream::QueueEvent(
    MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    return m_eventQueue ? m_eventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaStream::GetMediaSource(IMFMediaSource** ppMediaSource) {
    if (!ppMediaSource) return E_POINTER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown || !m_parentRef) return MF_E_SHUTDOWN;
    *ppMediaSource = m_parentRef.Get();
    (*ppMediaSource)->AddRef();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) {
    if (!ppStreamDescriptor) return E_POINTER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown || !m_streamDescriptor) return MF_E_SHUTDOWN;
    *ppStreamDescriptor = m_streamDescriptor.Get();
    (*ppStreamDescriptor)->AddRef();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaStream::RequestSample(IUnknown* pToken) {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (!m_active) return MF_E_INVALIDREQUEST;

    ComPtr<IUnknown> tok(pToken);
    m_pendingTokens.push_back(tok);
    m_cv.notify_one();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaStream::GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) {
    if (!ppvObject) return E_POINTER;
    if (guidService == GUID_NULL || guidService == MF_MEDIASOURCE_SERVICE) {
        return QueryInterface(riid, ppvObject);
    }
    return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP PhoneCamMediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP PhoneCamMediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP PhoneCamMediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

void PhoneCamMediaStream::DeliveryWorkerLoop() {
    LARGE_INTEGER freq{};
    QueryPerformanceFrequency(&freq);
    LARGE_INTEGER nextFrameTick{};
    QueryPerformanceCounter(&nextFrameTick);
    int64_t lastDeliveredWriteSeq = -1;

    while (true) {
        ComPtr<IUnknown> token;
        uint32_t fps = 30;
        {
            std::unique_lock<std::mutex> lock(m_lock);
            m_cv.wait(lock, [this]() {
                return m_shutdown || (m_active && !m_pendingTokens.empty());
            });
            if (m_shutdown) break;
            if (!m_active || m_pendingTokens.empty()) continue;
            token = m_pendingTokens.front();
            m_pendingTokens.pop_front();
            fps = m_fps;
        }

        // Wait briefly for either the next frame interval or a fresh frame in shared memory.
        const int64_t frameIntervalTicks = freq.QuadPart / static_cast<int64_t>(fps > 0 ? fps : 30);
        LARGE_INTEGER nowTick{};
        QueryPerformanceCounter(&nowTick);
        if (nowTick.QuadPart < nextFrameTick.QuadPart) {
            const double waitMs = static_cast<double>(nextFrameTick.QuadPart - nowTick.QuadPart) * 1000.0 / freq.QuadPart;
            if (waitMs >= 1.5 && waitMs <= 100.0) {
                DWORD sleepMs = static_cast<DWORD>(waitMs);
                // Wake early if a brand-new frame arrived from C#.
                for (DWORD elapsed = 0; elapsed < sleepMs; elapsed += 2) {
                    {
                        std::unique_lock<std::mutex> lock(m_lock);
                        if (m_shutdown || !m_active) break;
                    }
                    if (m_shmView != nullptr) {
                        const auto* hdr = reinterpret_cast<const PhoneCamShared::SharedHeader*>(m_shmView);
                        if (hdr->magic == PhoneCamShared::MAGIC && hdr->writeSeq != lastDeliveredWriteSeq) {
                            break;
                        }
                    }
                    Sleep(2);
                }
            }
        }
        QueryPerformanceCounter(&nowTick);
        nextFrameTick.QuadPart = nowTick.QuadPart + frameIntervalTicks;

        {
            std::unique_lock<std::mutex> lock(m_lock);
            if (m_shutdown || !m_active) continue;
        }

        if (m_shmView != nullptr) {
            const auto* hdr = reinterpret_cast<const PhoneCamShared::SharedHeader*>(m_shmView);
            if (hdr->magic == PhoneCamShared::MAGIC) {
                lastDeliveredWriteSeq = hdr->writeSeq;
            }
        }

        ComPtr<IMFSample> sample;
        HRESULT hr = BuildSampleForFrame(token.Get(), &sample);
        if (SUCCEEDED(hr) && sample) {
            m_eventQueue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
        }
    }
}

void PhoneCamMediaStream::TryOpenSharedMemory() {
    if (m_shmView != nullptr) return;

    const int64_t now = static_cast<int64_t>(GetTickCount64());
    if (now - m_lastShmTryTick < 500) return;
    m_lastShmTryTick = now;

    DWORD access = GENERIC_READ | GENERIC_WRITE;
    HANDLE hFile = CreateFileW(
        PhoneCamShared::SHM_FILE_PATH,
        access,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    bool writable = true;
    if (hFile == INVALID_HANDLE_VALUE) {
        access = GENERIC_READ;
        writable = false;
        hFile = CreateFileW(
            PhoneCamShared::SHM_FILE_PATH,
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
    }
    if (hFile == INVALID_HANDLE_VALUE) return;

    HANDLE hMap = CreateFileMappingW(
        hFile,
        nullptr,
        writable ? PAGE_READWRITE : PAGE_READONLY,
        0,
        0,
        nullptr);
    if (!hMap) {
        CloseHandle(hFile);
        return;
    }

    void* view = MapViewOfFile(
        hMap,
        writable ? FILE_MAP_ALL_ACCESS : FILE_MAP_READ,
        0,
        0,
        PhoneCamShared::TOTAL_SHM_SIZE);
    if (!view) {
        CloseHandle(hMap);
        CloseHandle(hFile);
        return;
    }

    m_shmFile = hFile;
    m_shmMap = hMap;
    m_shmView = static_cast<uint8_t*>(view);
    m_shmWritable = writable;
}

void PhoneCamMediaStream::CloseSharedMemory() {
    if (m_shmView != nullptr) {
        UnmapViewOfFile(m_shmView);
        m_shmView = nullptr;
    }
    if (m_shmMap != nullptr) {
        CloseHandle(m_shmMap);
        m_shmMap = nullptr;
    }
    if (m_shmFile != INVALID_HANDLE_VALUE) {
        CloseHandle(m_shmFile);
        m_shmFile = INVALID_HANDLE_VALUE;
    }
    m_shmWritable = false;
}

HRESULT PhoneCamMediaStream::BuildSampleForFrame(IUnknown* pToken, IMFSample** ppSample) {
    *ppSample = nullptr;

    TryOpenSharedMemory();

    const uint32_t outW = m_width;
    const uint32_t outH = m_height;
    const uint32_t sampleBytes = outW * outH * 3 / 2;

    ComPtr<IMFSample> sample;
    HRESULT hr = MFCreateSample(&sample);
    if (FAILED(hr)) return hr;

    ComPtr<IMFMediaBuffer> buffer;
    hr = MFCreateMemoryBuffer(sampleBytes, &buffer);
    if (FAILED(hr)) return hr;

    BYTE* dst = nullptr;
    hr = buffer->Lock(&dst, nullptr, nullptr);
    if (FAILED(hr)) return hr;

    bool copiedLive = false;
    if (m_shmView != nullptr) {
        auto* hdr = reinterpret_cast<PhoneCamShared::SharedHeader*>(m_shmView);
        if (hdr->magic == PhoneCamShared::MAGIC && hdr->version == PhoneCamShared::VERSION) {
            if (m_shmWritable) {
                hdr->requestedWidth = static_cast<int32_t>(outW);
                hdr->requestedHeight = static_cast<int32_t>(outH);
            }

            const int64_t ageMs = static_cast<int64_t>(GetTickCount64()) - hdr->lastProducerTickMs;
            const int32_t slot = hdr->latestSlot;
            const int32_t srcW = hdr->frameWidth;
            const int32_t srcH = hdr->frameHeight;

            if (ageMs >= 0 && ageMs <= 2000 &&
                slot >= 0 && slot < static_cast<int32_t>(PhoneCamShared::SLOT_COUNT) &&
                srcW > 0 && srcH > 0)
            {
                const size_t srcBytes = static_cast<size_t>(srcW) * static_cast<size_t>(srcH) * 3 / 2;
                if (srcBytes <= PhoneCamShared::SLOT_SIZE) {
                    const uint8_t* slotPtr = m_shmView + PhoneCamShared::HEADER_SIZE +
                        static_cast<size_t>(slot) * PhoneCamShared::SLOT_SIZE;
                    CopyResizedNv12(slotPtr, static_cast<uint32_t>(srcW), static_cast<uint32_t>(srcH), dst, outW, outH);
                    copiedLive = true;
                }
            }
        }
    }

    if (!copiedLive) {
        FillStandbyNv12(dst, outW, outH, m_frameCounter);
    }

    buffer->Unlock();
    buffer->SetCurrentLength(sampleBytes);
    sample->AddBuffer(buffer.Get());

    const LONGLONG now100ns = MFGetSystemTime();
    const LONGLONG duration100ns = 10'000'000LL / static_cast<LONGLONG>(m_fps > 0 ? m_fps : 30);
    sample->SetSampleTime(now100ns);
    sample->SetSampleDuration(duration100ns);

    if (pToken) {
        sample->SetUnknown(MFSampleExtension_Token, pToken);
    }

    ++m_frameCounter;
    *ppSample = sample.Detach();
    return S_OK;
}

void PhoneCamMediaStream::FillStandbyNv12(uint8_t* dst, uint32_t width, uint32_t height, uint64_t frameIndex) {
    const size_t ySize = static_cast<size_t>(width) * height;
    std::memset(dst, 24, ySize);
    std::memset(dst + ySize, 128, ySize / 2);

    // Subtle moving horizontal pulse bar so clients see a live stream before phone connects.
    const uint32_t barW = width / 6;
    const uint32_t barX = static_cast<uint32_t>((frameIndex * 12) % (width - barW));
    const uint32_t barY0 = height / 2 - 4;
    for (uint32_t y = barY0; y < barY0 + 8 && y < height; ++y) {
        std::memset(dst + static_cast<size_t>(y) * width + barX, 110, barW);
    }
}

// ============================================================================
// PhoneCamMediaSource
// ============================================================================

PhoneCamMediaSource::PhoneCamMediaSource() {
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

PhoneCamMediaSource::~PhoneCamMediaSource() {
    g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
}

HRESULT PhoneCamMediaSource::Initialize() {
    HRESULT hr = MFCreateEventQueue(&m_eventQueue);
    if (FAILED(hr)) return hr;

    if (FAILED(hr = MFCreateAttributes(&m_sourceAttributes, 4))) return hr;
    m_sourceAttributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    m_sourceAttributes->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, L"PhoneCam");

    if (FAILED(hr = MFCreateAttributes(&m_streamAttributes, 6))) return hr;
    m_streamAttributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, kPinNameVideoCapture);
    m_streamAttributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    m_streamAttributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    m_streamAttributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);

    m_stream = Make<PhoneCamMediaStream>();
    if (!m_stream) return E_OUTOFMEMORY;
    if (FAILED(hr = m_stream->Initialize(this))) return hr;

    ComPtr<IMFStreamDescriptor> sd;
    if (FAILED(hr = m_stream->GetStreamDescriptor(&sd))) return hr;

    IMFStreamDescriptor* sdArr[1] = { sd.Get() };
    if (FAILED(hr = MFCreatePresentationDescriptor(1, sdArr, &m_presentationDescriptor))) return hr;
    if (FAILED(hr = m_presentationDescriptor->SelectStream(0))) return hr;

    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) {
    return m_eventQueue ? m_eventQueue->GetEvent(dwFlags, ppEvent) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaSource::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) {
    return m_eventQueue ? m_eventQueue->BeginGetEvent(pCallback, punkState) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaSource::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) {
    return m_eventQueue ? m_eventQueue->EndGetEvent(pResult, ppEvent) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaSource::QueueEvent(
    MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
    return m_eventQueue ? m_eventQueue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue) : MF_E_SHUTDOWN;
}

IFACEMETHODIMP PhoneCamMediaSource::GetCharacteristics(DWORD* pdwCharacteristics) {
    if (!pdwCharacteristics) return E_POINTER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    *pdwCharacteristics = MFMEDIASOURCE_IS_LIVE;
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) {
    if (!ppPresentationDescriptor) return E_POINTER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown || !m_presentationDescriptor) return MF_E_SHUTDOWN;
    return m_presentationDescriptor->Clone(ppPresentationDescriptor);
}

IFACEMETHODIMP PhoneCamMediaSource::Start(
    IMFPresentationDescriptor* pPresentationDescriptor,
    const GUID* pguidTimeFormat,
    const PROPVARIANT* pvarStartPosition)
{
    if (!pPresentationDescriptor) return E_INVALIDARG;
    if (pguidTimeFormat && *pguidTimeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;

    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;

    BOOL selected = FALSE;
    ComPtr<IMFStreamDescriptor> sd;
    HRESULT hr = pPresentationDescriptor->GetStreamDescriptorByIndex(0, &selected, &sd);
    if (FAILED(hr)) return hr;

    // Sync client-chosen media type onto our active stream descriptor.
    ComPtr<IMFMediaTypeHandler> clientHandler;
    ComPtr<IMFStreamDescriptor> ourSd;
    ComPtr<IMFMediaTypeHandler> ourHandler;
    if (SUCCEEDED(sd->GetMediaTypeHandler(&clientHandler)) &&
        SUCCEEDED(m_stream->GetStreamDescriptor(&ourSd)) &&
        SUCCEEDED(ourSd->GetMediaTypeHandler(&ourHandler)))
    {
        ComPtr<IMFMediaType> chosenType;
        if (SUCCEEDED(clientHandler->GetCurrentMediaType(&chosenType)) && chosenType) {
            ourHandler->SetCurrentMediaType(chosenType.Get());
        }
    }

    const MediaEventType streamEvt = m_everStarted ? MEUpdatedStream : MENewStream;
    m_everStarted = true;

    m_eventQueue->QueueEventParamUnk(
        streamEvt, GUID_NULL, S_OK, static_cast<IMFMediaStream*>(m_stream.Get()));

    PROPVARIANT emptyVar;
    PropVariantInit(&emptyVar);
    const PROPVARIANT* pv = pvarStartPosition ? pvarStartPosition : &emptyVar;
    m_eventQueue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, pv);

    return m_stream->Start(pv);
}

IFACEMETHODIMP PhoneCamMediaSource::Stop() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (m_stream) m_stream->Stop();
    return m_eventQueue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP PhoneCamMediaSource::Pause() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return MF_E_SHUTDOWN;
    if (m_stream) m_stream->Pause();
    return m_eventQueue->QueueEventParamVar(MESourcePaused, GUID_NULL, S_OK, nullptr);
}

IFACEMETHODIMP PhoneCamMediaSource::Shutdown() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown) return S_OK;
    m_shutdown = true;

    if (m_stream) {
        m_stream->Shutdown();
        m_stream.Reset();
    }
    if (m_eventQueue) {
        m_eventQueue->Shutdown();
        m_eventQueue.Reset();
    }
    m_presentationDescriptor.Reset();
    m_sourceAttributes.Reset();
    m_streamAttributes.Reset();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::GetSourceAttributes(IMFAttributes** ppAttributes) {
    if (!ppAttributes) return E_POINTER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown || !m_sourceAttributes) return MF_E_SHUTDOWN;
    *ppAttributes = m_sourceAttributes.Get();
    (*ppAttributes)->AddRef();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) {
    if (!ppAttributes) return E_POINTER;
    if (dwStreamIdentifier != 0) return MF_E_INVALIDSTREAMNUMBER;
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_shutdown || !m_streamAttributes) return MF_E_SHUTDOWN;
    *ppAttributes = m_streamAttributes.Get();
    (*ppAttributes)->AddRef();
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::SetD3DManager(IUnknown*) {
    return S_OK;
}

IFACEMETHODIMP PhoneCamMediaSource::GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) {
    if (!ppvObject) return E_POINTER;
    if (guidService == GUID_NULL || guidService == MF_MEDIASOURCE_SERVICE) {
        return QueryInterface(riid, ppvObject);
    }
    return MF_E_UNSUPPORTED_SERVICE;
}

IFACEMETHODIMP PhoneCamMediaSource::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP PhoneCamMediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

IFACEMETHODIMP PhoneCamMediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG* BytesReturned) {
    if (BytesReturned) *BytesReturned = 0;
    return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

// ============================================================================
// PhoneCamActivator (IMFActivate)
// ============================================================================

PhoneCamActivator::PhoneCamActivator() {
    g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
}

PhoneCamActivator::~PhoneCamActivator() {
    g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
}

HRESULT PhoneCamActivator::Initialize() {
    HRESULT hr = MFCreateAttributes(&m_attributes, 4);
    if (FAILED(hr)) return hr;
    m_attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    m_attributes->SetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, L"PhoneCam");
    return S_OK;
}

IFACEMETHODIMP PhoneCamActivator::GetItem(REFGUID k, PROPVARIANT* v) { return m_attributes->GetItem(k, v); }
IFACEMETHODIMP PhoneCamActivator::GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) { return m_attributes->GetItemType(k, t); }
IFACEMETHODIMP PhoneCamActivator::CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) { return m_attributes->CompareItem(k, v, r); }
IFACEMETHODIMP PhoneCamActivator::Compare(IMFAttributes* p, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) { return m_attributes->Compare(p, m, r); }
IFACEMETHODIMP PhoneCamActivator::GetUINT32(REFGUID k, UINT32* v) { return m_attributes->GetUINT32(k, v); }
IFACEMETHODIMP PhoneCamActivator::GetUINT64(REFGUID k, UINT64* v) { return m_attributes->GetUINT64(k, v); }
IFACEMETHODIMP PhoneCamActivator::GetDouble(REFGUID k, double* v) { return m_attributes->GetDouble(k, v); }
IFACEMETHODIMP PhoneCamActivator::GetGUID(REFGUID k, GUID* v) { return m_attributes->GetGUID(k, v); }
IFACEMETHODIMP PhoneCamActivator::GetStringLength(REFGUID k, UINT32* l) { return m_attributes->GetStringLength(k, l); }
IFACEMETHODIMP PhoneCamActivator::GetString(REFGUID k, LPWSTR s, UINT32 c, UINT32* l) { return m_attributes->GetString(k, s, c, l); }
IFACEMETHODIMP PhoneCamActivator::GetAllocatedString(REFGUID k, LPWSTR* s, UINT32* l) { return m_attributes->GetAllocatedString(k, s, l); }
IFACEMETHODIMP PhoneCamActivator::GetBlobSize(REFGUID k, UINT32* s) { return m_attributes->GetBlobSize(k, s); }
IFACEMETHODIMP PhoneCamActivator::GetBlob(REFGUID k, UINT8* b, UINT32 c, UINT32* s) { return m_attributes->GetBlob(k, b, c, s); }
IFACEMETHODIMP PhoneCamActivator::GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) { return m_attributes->GetAllocatedBlob(k, b, s); }
IFACEMETHODIMP PhoneCamActivator::GetUnknown(REFGUID k, REFIID r, LPVOID* p) { return m_attributes->GetUnknown(k, r, p); }
IFACEMETHODIMP PhoneCamActivator::SetItem(REFGUID k, REFPROPVARIANT v) { return m_attributes->SetItem(k, v); }
IFACEMETHODIMP PhoneCamActivator::DeleteItem(REFGUID k) { return m_attributes->DeleteItem(k); }
IFACEMETHODIMP PhoneCamActivator::DeleteAllItems() { return m_attributes->DeleteAllItems(); }
IFACEMETHODIMP PhoneCamActivator::SetUINT32(REFGUID k, UINT32 v) { return m_attributes->SetUINT32(k, v); }
IFACEMETHODIMP PhoneCamActivator::SetUINT64(REFGUID k, UINT64 v) { return m_attributes->SetUINT64(k, v); }
IFACEMETHODIMP PhoneCamActivator::SetDouble(REFGUID k, double v) { return m_attributes->SetDouble(k, v); }
IFACEMETHODIMP PhoneCamActivator::SetGUID(REFGUID k, REFGUID v) { return m_attributes->SetGUID(k, v); }
IFACEMETHODIMP PhoneCamActivator::SetString(REFGUID k, LPCWSTR v) { return m_attributes->SetString(k, v); }
IFACEMETHODIMP PhoneCamActivator::SetBlob(REFGUID k, const UINT8* b, UINT32 s) { return m_attributes->SetBlob(k, b, s); }
IFACEMETHODIMP PhoneCamActivator::SetUnknown(REFGUID k, IUnknown* p) { return m_attributes->SetUnknown(k, p); }
IFACEMETHODIMP PhoneCamActivator::LockStore() { return m_attributes->LockStore(); }
IFACEMETHODIMP PhoneCamActivator::UnlockStore() { return m_attributes->UnlockStore(); }
IFACEMETHODIMP PhoneCamActivator::GetCount(UINT32* c) { return m_attributes->GetCount(c); }
IFACEMETHODIMP PhoneCamActivator::GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) { return m_attributes->GetItemByIndex(i, k, v); }
IFACEMETHODIMP PhoneCamActivator::CopyAllItems(IMFAttributes* d) { return m_attributes->CopyAllItems(d); }

IFACEMETHODIMP PhoneCamActivator::ActivateObject(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    std::unique_lock<std::mutex> lock(m_lock);
    if (!m_source) {
        ComPtr<PhoneCamMediaSource> src = Make<PhoneCamMediaSource>();
        if (!src) return E_OUTOFMEMORY;
        HRESULT hr = src->Initialize();
        if (FAILED(hr)) return hr;
        m_source = src;
    }
    return m_source->QueryInterface(riid, ppv);
}

IFACEMETHODIMP PhoneCamActivator::ShutdownObject() {
    std::unique_lock<std::mutex> lock(m_lock);
    if (m_source) {
        m_source->Shutdown();
        m_source.Reset();
    }
    return S_OK;
}

IFACEMETHODIMP PhoneCamActivator::DetachObject() {
    std::unique_lock<std::mutex> lock(m_lock);
    m_source.Reset();
    return S_OK;
}

// ============================================================================
// ClassFactory & COM DLL Exports
// ============================================================================

class PhoneCamClassFactory final :
    public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        IClassFactory>
{
public:
    PhoneCamClassFactory() { g_dllRefCount.fetch_add(1, std::memory_order_relaxed); }
    ~PhoneCamClassFactory() override { g_dllRefCount.fetch_sub(1, std::memory_order_relaxed); }

    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppvObject) override {
        if (!ppvObject) return E_POINTER;
        *ppvObject = nullptr;
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;

        if (riid == __uuidof(IMFMediaSource) ||
            riid == __uuidof(IMFMediaSourceEx) ||
            riid == __uuidof(IMFMediaEventGenerator))
        {
            ComPtr<PhoneCamMediaSource> source = Make<PhoneCamMediaSource>();
            if (!source) return E_OUTOFMEMORY;
            HRESULT hr = source->Initialize();
            if (FAILED(hr)) return hr;
            return source->QueryInterface(riid, ppvObject);
        }

        ComPtr<PhoneCamActivator> activator = Make<PhoneCamActivator>();
        if (!activator) return E_OUTOFMEMORY;
        HRESULT hr = activator->Initialize();
        if (FAILED(hr)) return hr;
        return activator->QueryInterface(riid, ppvObject);
    }

    IFACEMETHODIMP LockServer(BOOL fLock) override {
        if (fLock) g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
        else g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        return S_OK;
    }
};

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

STDAPI DllCanUnloadNow() {
    return g_dllRefCount.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (rclsid != CLSID_PhoneCamSource) return CLASS_E_CLASSNOTAVAILABLE;

    ComPtr<PhoneCamClassFactory> factory = Make<PhoneCamClassFactory>();
    if (!factory) return E_OUTOFMEMORY;
    return factory->QueryInterface(riid, ppv);
}

STDAPI DllRegisterServer() {
    wchar_t modulePath[MAX_PATH]{};
    DWORD len = GetModuleFileNameW(g_hModule, modulePath, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) return HRESULT_FROM_WIN32(GetLastError());

    const wchar_t* subKey = L"Software\\Classes\\CLSID\\{7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}\\InprocServer32";
    HKEY hKey = nullptr;
    LSTATUS st = RegCreateKeyExW(
        HKEY_LOCAL_MACHINE,
        subKey,
        0,
        nullptr,
        REG_OPTION_NON_VOLATILE,
        KEY_WRITE,
        nullptr,
        &hKey,
        nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);

    st = RegSetValueExW(
        hKey,
        nullptr,
        0,
        REG_SZ,
        reinterpret_cast<const BYTE*>(modulePath),
        static_cast<DWORD>((wcslen(modulePath) + 1) * sizeof(wchar_t)));
    if (st == ERROR_SUCCESS) {
        const wchar_t* threading = L"Both";
        st = RegSetValueExW(
            hKey,
            L"ThreadingModel",
            0,
            REG_SZ,
            reinterpret_cast<const BYTE*>(threading),
            static_cast<DWORD>((wcslen(threading) + 1) * sizeof(wchar_t)));
    }
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(st);
}

STDAPI DllUnregisterServer() {
    LSTATUS st = RegDeleteTreeW(
        HKEY_LOCAL_MACHINE,
        L"Software\\Classes\\CLSID\\{7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}");
    if (st == ERROR_FILE_NOT_FOUND) return S_OK;
    return HRESULT_FROM_WIN32(st);
}

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mferror.h>
#include <ks.h>
#include <ksproxy.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <thread>
#include <vector>

#include "SharedFrameMemory.h"

// {7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}
static const GUID CLSID_PhoneCamSource = {
    0x7b5e9a41, 0x2c3d, 0x4f88, { 0x9a, 0x1e, 0x4d, 0x5f, 0x6a, 0x7b, 0x8c, 0x9d }
};

class PhoneCamMediaSource;

class PhoneCamMediaStream final :
    public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFMediaStream, IMFMediaEventGenerator>,
        IMFGetService,
        IKsControl>
{
public:
    PhoneCamMediaStream();
    ~PhoneCamMediaStream() override;

    HRESULT Initialize(PhoneCamMediaSource* parentSource);
    HRESULT Start(const PROPVARIANT* pvarStartPosition);
    HRESULT Stop();
    HRESULT Pause();
    HRESULT Shutdown();

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** ppMediaSource) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* pToken) override;

    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

    // IKsControl
    IFACEMETHODIMP KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override;

private:
    void DeliveryWorkerLoop();
    void TryOpenSharedMemory();
    void CloseSharedMemory();
    HRESULT BuildSampleForFrame(IUnknown* pToken, IMFSample** ppSample);
    void FillStandbyNv12(uint8_t* dst, uint32_t width, uint32_t height, uint64_t frameIndex);

    std::mutex m_lock;
    std::condition_variable m_cv;
    bool m_shutdown = false;
    bool m_active = false;
    bool m_workerRunning = false;
    std::thread m_worker;
    std::deque<Microsoft::WRL::ComPtr<IUnknown>> m_pendingTokens;

    PhoneCamMediaSource* m_parentRaw = nullptr; // weak back-pointer; cleared on Shutdown
    Microsoft::WRL::ComPtr<IMFMediaSource> m_parentRef;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_eventQueue;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> m_streamDescriptor;

    uint32_t m_width = 1280;
    uint32_t m_height = 720;
    uint32_t m_fps = 30;
    uint64_t m_frameCounter = 0;
    int64_t m_lastShmTryTick = 0;

    HANDLE m_shmFile = INVALID_HANDLE_VALUE;
    HANDLE m_shmMap = nullptr;
    uint8_t* m_shmView = nullptr;
    bool m_shmWritable = false;
};

class PhoneCamMediaSource final :
    public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
        IMFGetService,
        IKsControl>
{
public:
    PhoneCamMediaSource();
    ~PhoneCamMediaSource() override;

    HRESULT Initialize();

    // IMFMediaEventGenerator
    IFACEMETHODIMP GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent) override;
    IFACEMETHODIMP QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue) override;

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* pdwCharacteristics) override;
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPresentationDescriptor) override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* pPresentationDescriptor, const GUID* pguidTimeFormat, const PROPVARIANT* pvarStartPosition) override;
    IFACEMETHODIMP Stop() override;
    IFACEMETHODIMP Pause() override;
    IFACEMETHODIMP Shutdown() override;

    // IMFMediaSourceEx
    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** ppAttributes) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD dwStreamIdentifier, IMFAttributes** ppAttributes) override;
    IFACEMETHODIMP SetD3DManager(IUnknown* pManager) override;

    // IMFGetService
    IFACEMETHODIMP GetService(REFGUID guidService, REFIID riid, LPVOID* ppvObject) override;

    // IKsControl
    IFACEMETHODIMP KsProperty(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned) override;

private:
    std::mutex m_lock;
    bool m_shutdown = false;
    bool m_everStarted = false;

    Microsoft::WRL::ComPtr<IMFMediaEventQueue> m_eventQueue;
    Microsoft::WRL::ComPtr<IMFAttributes> m_sourceAttributes;
    Microsoft::WRL::ComPtr<IMFAttributes> m_streamAttributes;
    Microsoft::WRL::ComPtr<IMFPresentationDescriptor> m_presentationDescriptor;
    Microsoft::WRL::ComPtr<PhoneCamMediaStream> m_stream;
};

class PhoneCamActivator final :
    public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>>
{
public:
    PhoneCamActivator();
    ~PhoneCamActivator() override;

    HRESULT Initialize();

    // IMFAttributes (delegated to m_attributes)
    IFACEMETHODIMP GetItem(REFGUID guidKey, PROPVARIANT* pValue) override;
    IFACEMETHODIMP GetItemType(REFGUID guidKey, MF_ATTRIBUTE_TYPE* pType) override;
    IFACEMETHODIMP CompareItem(REFGUID guidKey, REFPROPVARIANT Value, BOOL* pbResult) override;
    IFACEMETHODIMP Compare(IMFAttributes* pTheirs, MF_ATTRIBUTES_MATCH_TYPE MatchType, BOOL* pbResult) override;
    IFACEMETHODIMP GetUINT32(REFGUID guidKey, UINT32* punValue) override;
    IFACEMETHODIMP GetUINT64(REFGUID guidKey, UINT64* punValue) override;
    IFACEMETHODIMP GetDouble(REFGUID guidKey, double* pfValue) override;
    IFACEMETHODIMP GetGUID(REFGUID guidKey, GUID* pguidValue) override;
    IFACEMETHODIMP GetStringLength(REFGUID guidKey, UINT32* pcchLength) override;
    IFACEMETHODIMP GetString(REFGUID guidKey, LPWSTR pwszValue, UINT32 cchBufSize, UINT32* pcchLength) override;
    IFACEMETHODIMP GetAllocatedString(REFGUID guidKey, LPWSTR* ppwszValue, UINT32* pcchLength) override;
    IFACEMETHODIMP GetBlobSize(REFGUID guidKey, UINT32* pcbBlobSize) override;
    IFACEMETHODIMP GetBlob(REFGUID guidKey, UINT8* pBuf, UINT32 cbBufSize, UINT32* pcbBlobSize) override;
    IFACEMETHODIMP GetAllocatedBlob(REFGUID guidKey, UINT8** ppBuf, UINT32* pcbSize) override;
    IFACEMETHODIMP GetUnknown(REFGUID guidKey, REFIID riid, LPVOID* ppv) override;
    IFACEMETHODIMP SetItem(REFGUID guidKey, REFPROPVARIANT Value) override;
    IFACEMETHODIMP DeleteItem(REFGUID guidKey) override;
    IFACEMETHODIMP DeleteAllItems() override;
    IFACEMETHODIMP SetUINT32(REFGUID guidKey, UINT32 unValue) override;
    IFACEMETHODIMP SetUINT64(REFGUID guidKey, UINT64 unValue) override;
    IFACEMETHODIMP SetDouble(REFGUID guidKey, double fValue) override;
    IFACEMETHODIMP SetGUID(REFGUID guidKey, REFGUID guidValue) override;
    IFACEMETHODIMP SetString(REFGUID guidKey, LPCWSTR wszValue) override;
    IFACEMETHODIMP SetBlob(REFGUID guidKey, const UINT8* pBuf, UINT32 cbBufSize) override;
    IFACEMETHODIMP SetUnknown(REFGUID guidKey, IUnknown* pUnknown) override;
    IFACEMETHODIMP LockStore() override;
    IFACEMETHODIMP UnlockStore() override;
    IFACEMETHODIMP GetCount(UINT32* pcItems) override;
    IFACEMETHODIMP GetItemByIndex(UINT32 unIndex, GUID* pguidKey, PROPVARIANT* pValue) override;
    IFACEMETHODIMP CopyAllItems(IMFAttributes* pDest) override;

    // IMFActivate
    IFACEMETHODIMP ActivateObject(REFIID riid, void** ppv) override;
    IFACEMETHODIMP ShutdownObject() override;
    IFACEMETHODIMP DetachObject() override;

private:
    std::mutex m_lock;
    Microsoft::WRL::ComPtr<IMFAttributes> m_attributes;
    Microsoft::WRL::ComPtr<PhoneCamMediaSource> m_source;
};


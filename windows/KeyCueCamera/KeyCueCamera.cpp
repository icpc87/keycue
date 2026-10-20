// KeyCue の仮想カメラ（Windows 11 の Media Foundation 仮想カメラ）。
//
// ひとつの DLL が 2 つの役をする:
//  1. カメラ本体（COM のメディアソース）。Windows の Frame Server（LocalService のサービス）が読み込み、
//     Zoom・Teams・カメラアプリなどへ映像を出す。映像は共有メモリ「Global\KeyCueVirtualCamera」から読む。
//  2. KeyCue.exe から呼ぶ関数（KeyCueCameraCreate / KeyCueCameraDestroy）。仮想カメラ「KeyCue」を作って始める。
//
// KeyCue は共有メモリに BGRA 1920×1080 のコマを書く（seqlock：書く前と後に通し番号を 1 ずつ増やす）。
// 1 秒以上新しいコマが来なければ黒を出す。
//
// ビルド: build.sh（llvm-mingw で x64 / arm64）

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#define WINVER 0x0A00
#include <windows.h>
#include <sddl.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <atomic>
#include <mutex>
#include <cstdint>
#include <cstring>
#include <new>

// ---- GUID・インターフェース（mingw のヘッダーにないもの） ----

// {6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D51}
static const CLSID CLSID_KeyCueCamera = {0x6b3e7c2a, 0x4d1f, 0x4e8b, {0x9c, 0x55, 0x2f, 0x7a, 0x1b, 0x0e, 0x9d, 0x51}};
static const wchar_t kClsidString[] = L"{6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D51}";
static const GUID KC_PINNAME_VIDEO_CAPTURE = {0xfb6c4281, 0x0353, 0x11d1, {0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba}};
static const GUID KC_MF_DEVICESTREAM_STREAM_CATEGORY = {0x2939e7b8, 0xa62e, 0x4579, {0xb6, 0x74, 0xd4, 0x07, 0x3d, 0xfa, 0xbb, 0xba}};
static const GUID KC_MF_DEVICESTREAM_STREAM_ID = {0x11bd5120, 0xd124, 0x446b, {0x88, 0xe6, 0x17, 0x06, 0x02, 0x57, 0xff, 0xf9}};
static const GUID KC_MF_DEVICESTREAM_FRAMESERVER_SHARED = {0x1cb378e9, 0xb279, 0x41d4, {0xaf, 0x97, 0x34, 0xa2, 0x43, 0xe6, 0x83, 0x20}};
static const GUID KC_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES = {0x17145fd1, 0x1b2b, 0x423c, {0x80, 0x01, 0x2b, 0x68, 0x33, 0xed, 0x35, 0x88}};
static const GUID KC_MFSampleExtension_Token = {0x8294da66, 0xf328, 0x4805, {0xb5, 0x51, 0x00, 0xde, 0xb4, 0xc5, 0x7a, 0x61}};
static const IID KC_IID_IMFMediaStream2 = {0xc5bc37d6, 0x75c7, 0x46a1, {0xa1, 0x32, 0x81, 0xb5, 0xf7, 0x23, 0xc2, 0x0f}};
static const IID KC_IID_IKsControl = {0x28f54685, 0x06fd, 0x11d2, {0xb2, 0x7a, 0x00, 0xa0, 0xc9, 0x22, 0x31, 0x96}};
static const UINT32 kFrameSourceTypesColor = 0x0001;

enum KC_MF_STREAM_STATE { KC_MF_STREAM_STATE_STOPPED = 0, KC_MF_STREAM_STATE_PAUSED = 1, KC_MF_STREAM_STATE_RUNNING = 2 };

struct IMFMediaStream2Local : public IMFMediaStream {
    virtual HRESULT STDMETHODCALLTYPE SetStreamState(KC_MF_STREAM_STATE value) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetStreamState(KC_MF_STREAM_STATE* value) = 0;
};

struct IKsControlLocal : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE KsProperty(void* property, ULONG propertyLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsMethod(void* method, ULONG methodLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsEvent(void* event, ULONG eventLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
};

// mfvirtualcamera.h
enum KC_MFVirtualCameraType { KC_MFVirtualCameraType_SoftwareCameraSource = 0 };
enum KC_MFVirtualCameraLifetime { KC_MFVirtualCameraLifetime_Session = 0, KC_MFVirtualCameraLifetime_System = 1 };
enum KC_MFVirtualCameraAccess { KC_MFVirtualCameraAccess_CurrentUser = 0, KC_MFVirtualCameraAccess_AllUsers = 1 };

struct IMFVirtualCameraLocal : public IMFAttributes {
    virtual HRESULT STDMETHODCALLTYPE AddDeviceSourceInfo(LPCWSTR info) = 0;
    virtual HRESULT STDMETHODCALLTYPE AddProperty(const void* key, ULONG type, const BYTE* data, ULONG size) = 0;
    virtual HRESULT STDMETHODCALLTYPE AddRegistryEntry(LPCWSTR name, LPCWSTR subkey, DWORD regType, const BYTE* data, ULONG size) = 0;
    virtual HRESULT STDMETHODCALLTYPE Start(IMFAsyncCallback* callback) = 0;
    virtual HRESULT STDMETHODCALLTYPE Stop() = 0;
    virtual HRESULT STDMETHODCALLTYPE Remove() = 0;
    virtual HRESULT STDMETHODCALLTYPE GetMediaSource(IMFMediaSource** source) = 0;
    virtual HRESULT STDMETHODCALLTYPE SendCameraProperty(REFGUID set, ULONG id, ULONG flags, void* p, ULONG pl, void* d, ULONG dl, ULONG* r) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateSyncEvent(REFGUID set, ULONG id, ULONG flags, HANDLE ev, void** obj) = 0;
    virtual HRESULT STDMETHODCALLTYPE CreateSyncSemaphore(REFGUID set, ULONG id, ULONG flags, HANDLE sem, LONG count, void** obj) = 0;
    virtual HRESULT STDMETHODCALLTYPE Shutdown() = 0;
};

typedef HRESULT(WINAPI* MFCreateVirtualCameraFn)(KC_MFVirtualCameraType, KC_MFVirtualCameraLifetime, KC_MFVirtualCameraAccess,
                                                  LPCWSTR, LPCWSTR, const GUID*, ULONG, IMFVirtualCameraLocal**);

// ---- 共通 ----

static const UINT32 kWidth = 1920, kHeight = 1080, kFps = 30;
static std::atomic<long> g_objects{0};
static std::atomic<long> g_locks{0};
static HMODULE g_module = nullptr;

template <class T> static void SafeRelease(T*& p) {
    if (p) { p->Release(); p = nullptr; }
}

// ---- 共有メモリ（KeyCue → カメラ） ----

#pragma pack(push, 8)
struct SharedHeader {
    uint32_t magic;          // 'KCVC'
    uint32_t version;        // 1
    uint32_t width, height;  // 1920, 1080
    volatile LONG64 sequence;   // 書いている途中は奇数
    volatile LONG64 writtenAt;  // GetTickCount64（ミリ秒）
    uint32_t reserved[8];
};
#pragma pack(pop)
static const uint32_t kMagic = 0x4356434B;  // "KCVC"
static const size_t kHeaderSize = 64;
static const size_t kFrameBytes = (size_t)kWidth * kHeight * 4;

class SharedFrames {
public:
    SharedFrames() {
        // KeyCue（ふつうの利用者の権限）が開けるように、対話ユーザーにも読み書きを許す
        SECURITY_ATTRIBUTES sa = {sizeof(sa), nullptr, FALSE};
        PSECURITY_DESCRIPTOR sd = nullptr;
        if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GA;;;IU)(A;;GA;;;AU)",
                                                                 SDDL_REVISION_1, &sd, nullptr)) {
            sa.lpSecurityDescriptor = sd;
        }
        mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, sd ? &sa : nullptr, PAGE_READWRITE, 0,
                                      (DWORD)(kHeaderSize + kFrameBytes), L"Global\\KeyCueVirtualCamera");
        if (sd) LocalFree(sd);
        if (mapping_) view_ = (uint8_t*)MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, 0);
        if (view_) {
            auto* h = (SharedHeader*)view_;
            if (h->magic != kMagic) {
                h->width = kWidth;
                h->height = kHeight;
                h->version = 1;
                h->magic = kMagic;
            }
        }
    }
    ~SharedFrames() {
        if (view_) UnmapViewOfFile(view_);
        if (mapping_) CloseHandle(mapping_);
    }

    /// いちばん新しいコマを dst（BGRA・上から）に写す。新しいコマがなければ false
    bool Read(uint8_t* dst) {
        if (!view_) return false;
        auto* h = (SharedHeader*)view_;
        if (h->writtenAt == 0 || GetTickCount64() - (ULONGLONG)h->writtenAt > 1000) return false;
        for (int attempt = 0; attempt < 4; attempt++) {
            LONG64 s1 = InterlockedCompareExchange64(&h->sequence, 0, 0);
            if (s1 & 1) { YieldProcessor(); continue; }
            memcpy(dst, view_ + kHeaderSize, kFrameBytes);
            MemoryBarrier();
            LONG64 s2 = InterlockedCompareExchange64(&h->sequence, 0, 0);
            if (s1 == s2) return true;
        }
        return true;  // 書き込みと重なり続けた：少し崩れても出す
    }

private:
    HANDLE mapping_ = nullptr;
    uint8_t* view_ = nullptr;
};

// BGRA → NV12（BT.709・リミテッドレンジ）
static void BgraToNv12(const uint8_t* src, uint8_t* dst, UINT32 w, UINT32 h) {
    uint8_t* yPlane = dst;
    uint8_t* uv = dst + (size_t)w * h;
    for (UINT32 y = 0; y < h; y++) {
        const uint8_t* s = src + (size_t)y * w * 4;
        uint8_t* d = yPlane + (size_t)y * w;
        for (UINT32 x = 0; x < w; x++) {
            int b = s[x * 4], g = s[x * 4 + 1], r = s[x * 4 + 2];
            d[x] = (uint8_t)(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8));
        }
    }
    for (UINT32 y = 0; y < h; y += 2) {
        const uint8_t* s0 = src + (size_t)y * w * 4;
        const uint8_t* s1 = src + (size_t)(y + 1) * w * 4;
        uint8_t* d = uv + (size_t)(y / 2) * w;
        for (UINT32 x = 0; x < w; x += 2) {
            int b = (s0[x * 4] + s0[x * 4 + 4] + s1[x * 4] + s1[x * 4 + 4]) >> 2;
            int g = (s0[x * 4 + 1] + s0[x * 4 + 5] + s1[x * 4 + 1] + s1[x * 4 + 5]) >> 2;
            int r = (s0[x * 4 + 2] + s0[x * 4 + 6] + s1[x * 4 + 2] + s1[x * 4 + 6]) >> 2;
            int u = 128 + ((-26 * r - 87 * g + 112 * b + 128) >> 8);
            int v = 128 + ((112 * r - 102 * g - 10 * b + 128) >> 8);
            d[x] = (uint8_t)(u < 0 ? 0 : u > 255 ? 255 : u);
            d[x + 1] = (uint8_t)(v < 0 ? 0 : v > 255 ? 255 : v);
        }
    }
}

// ---- メディアストリーム ----

class MediaSource;

class MediaStream final : public IMFMediaStream2Local, public IKsControlLocal {
public:
    MediaStream(MediaSource* parent) : parent_(parent) { g_objects++; }

    HRESULT Initialize() {
        HRESULT hr = MFCreateAttributes(&attributes_, 8);
        if (FAILED(hr)) return hr;
        SetStreamAttributes(attributes_);

        IMFMediaType* types[2] = {};
        const GUID subtypes[2] = {MFVideoFormat_NV12, MFVideoFormat_RGB32};
        for (int i = 0; i < 2; i++) {
            hr = MFCreateMediaType(&types[i]);
            if (FAILED(hr)) break;
            types[i]->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            types[i]->SetGUID(MF_MT_SUBTYPE, subtypes[i]);
            MFSetAttributeSize(types[i], MF_MT_FRAME_SIZE, kWidth, kHeight);
            MFSetAttributeRatio(types[i], MF_MT_FRAME_RATE, kFps, 1);
            MFSetAttributeRatio(types[i], MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            types[i]->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            types[i]->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
            if (i == 0) {
                types[i]->SetUINT32(MF_MT_DEFAULT_STRIDE, kWidth);
                types[i]->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
                types[i]->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
                types[i]->SetUINT32(MF_MT_SAMPLE_SIZE, kWidth * kHeight * 3 / 2);
            } else {
                types[i]->SetUINT32(MF_MT_DEFAULT_STRIDE, kWidth * 4);  // 正 = 上から
                types[i]->SetUINT32(MF_MT_SAMPLE_SIZE, kWidth * kHeight * 4);
            }
            types[i]->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)((UINT64)kWidth * kHeight * (i == 0 ? 12 : 32) * kFps > 0xFFFFFFFFu
                                                                 ? 0xFFFFFFFFu : (UINT64)kWidth * kHeight * (i == 0 ? 12 : 32) * kFps));
        }
        if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, 2, types, &descriptor_);
        if (SUCCEEDED(hr)) {
            IMFMediaTypeHandler* handler = nullptr;
            hr = descriptor_->GetMediaTypeHandler(&handler);
            if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types[0]);
            SafeRelease(handler);
            SetStreamAttributes(descriptor_);
        }
        for (auto& t : types) SafeRelease(t);
        if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&queue_);
        return hr;
    }

    static void SetStreamAttributes(IMFAttributes* a) {
        a->SetGUID(KC_MF_DEVICESTREAM_STREAM_CATEGORY, KC_PINNAME_VIDEO_CAPTURE);
        a->SetUINT32(KC_MF_DEVICESTREAM_STREAM_ID, 0);
        a->SetUINT32(KC_MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
        a->SetUINT32(KC_MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, kFrameSourceTypesColor);
    }

    IMFAttributes* Attributes() { return attributes_; }

    HRESULT Start(IMFMediaType* type) {
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        GUID subtype = MFVideoFormat_NV12;
        if (type) type->GetGUID(MF_MT_SUBTYPE, &subtype);
        nv12_ = subtype == MFVideoFormat_NV12;
        if (!frame_) frame_ = new (std::nothrow) uint8_t[kFrameBytes];
        if (!frames_) frames_ = new (std::nothrow) SharedFrames();
        state_ = KC_MF_STREAM_STATE_RUNNING;
        return queue_->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr);
    }

    HRESULT Stop() {
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        state_ = KC_MF_STREAM_STATE_STOPPED;
        return queue_->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    void Shutdown() {
        std::lock_guard<std::mutex> g(lock_);
        if (queue_) queue_->Shutdown();
        SafeRelease(queue_);
        SafeRelease(descriptor_);
        SafeRelease(attributes_);
        delete frames_;
        frames_ = nullptr;
        delete[] frame_;
        frame_ = nullptr;
        parent_ = nullptr;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaStream || riid == KC_IID_IMFMediaStream2) {
            *ppv = static_cast<IMFMediaStream2Local*>(this);
        } else if (riid == KC_IID_IKsControl) {
            *ppv = static_cast<IKsControlLocal*>(this);
        } else {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override {
        long r = --refs_;
        if (r == 0) { delete this; }
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override {
        IMFMediaEventQueue* q;
        { std::lock_guard<std::mutex> g(lock_); q = queue_; if (!q) return MF_E_SHUTDOWN; q->AddRef(); }
        HRESULT hr = q->GetEvent(flags, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->BeginGetEvent(cb, state) : MF_E_SHUTDOWN;
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->EndGetEvent(result, ev) : MF_E_SHUTDOWN;
    }
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->QueueEventParamVar(type, ext, status, value) : MF_E_SHUTDOWN;
    }

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** sd) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!sd) return E_POINTER;
        if (!descriptor_) return MF_E_SHUTDOWN;
        *sd = descriptor_;
        descriptor_->AddRef();
        return S_OK;
    }
    STDMETHODIMP RequestSample(IUnknown* token) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        if (state_ != KC_MF_STREAM_STATE_RUNNING) return MF_E_MEDIA_SOURCE_WRONGSTATE;
        DWORD size = nv12_ ? kWidth * kHeight * 3 / 2 : (DWORD)kFrameBytes;
        IMFSample* sample = nullptr;
        IMFMediaBuffer* buffer = nullptr;
        HRESULT hr = MFCreateSample(&sample);
        if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(size, &buffer);
        if (SUCCEEDED(hr)) {
            BYTE* data = nullptr;
            hr = buffer->Lock(&data, nullptr, nullptr);
            if (SUCCEEDED(hr)) {
                bool fresh = frame_ && frames_ && frames_->Read(frame_);
                if (!fresh && frame_) {
                    // KeyCue から来ていない：黒（不透明）
                    uint32_t* p = (uint32_t*)frame_;
                    for (size_t i = 0; i < (size_t)kWidth * kHeight; i++) p[i] = 0xFF000000u;
                }
                if (frame_) {
                    if (nv12_) BgraToNv12(frame_, data, kWidth, kHeight);
                    else memcpy(data, frame_, kFrameBytes);
                }
                buffer->Unlock();
                buffer->SetCurrentLength(size);
                hr = sample->AddBuffer(buffer);
            }
        }
        if (SUCCEEDED(hr)) {
            sample->SetSampleTime(MFGetSystemTime());
            sample->SetSampleDuration(10000000 / kFps);
            if (token) sample->SetUnknown(KC_MFSampleExtension_Token, token);
            hr = queue_->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample);
        }
        SafeRelease(buffer);
        SafeRelease(sample);
        return hr;
    }

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(KC_MF_STREAM_STATE value) override {
        if (value == KC_MF_STREAM_STATE_PAUSED) return MF_E_INVALID_STATE_TRANSITION;
        std::lock_guard<std::mutex> g(lock_);
        state_ = value;
        return S_OK;
    }
    STDMETHODIMP GetStreamState(KC_MF_STREAM_STATE* value) override {
        if (!value) return E_POINTER;
        std::lock_guard<std::mutex> g(lock_);
        *value = state_;
        return S_OK;
    }

    // IKsControl（カメラの設定項目はない）
    STDMETHODIMP KsProperty(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

private:
    ~MediaStream() {
        Shutdown();
        g_objects--;
    }

    std::atomic<long> refs_{1};
    std::mutex lock_;
    MediaSource* parent_;
    IMFAttributes* attributes_ = nullptr;
    IMFStreamDescriptor* descriptor_ = nullptr;
    IMFMediaEventQueue* queue_ = nullptr;
    KC_MF_STREAM_STATE state_ = KC_MF_STREAM_STATE_STOPPED;
    bool nv12_ = true;
    uint8_t* frame_ = nullptr;
    SharedFrames* frames_ = nullptr;
};

// ---- メディアソース ----

class MediaSource final : public IMFMediaSourceEx, public IMFGetService, public IKsControlLocal {
public:
    MediaSource() { g_objects++; }

    HRESULT Initialize() {
        HRESULT hr = MFCreateAttributes(&attributes_, 4);
        if (FAILED(hr)) return hr;
        stream_ = new (std::nothrow) MediaStream(this);
        if (!stream_) return E_OUTOFMEMORY;
        hr = stream_->Initialize();
        if (FAILED(hr)) return hr;
        IMFStreamDescriptor* sd = nullptr;
        hr = stream_->GetStreamDescriptor(&sd);
        if (SUCCEEDED(hr)) hr = MFCreatePresentationDescriptor(1, &sd, &presentation_);
        if (SUCCEEDED(hr)) hr = presentation_->SelectStream(0);
        SafeRelease(sd);
        if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&queue_);
        return hr;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFMediaEventGenerator || riid == IID_IMFMediaSource || riid == IID_IMFMediaSourceEx) {
            *ppv = static_cast<IMFMediaSourceEx*>(this);
        } else if (riid == IID_IMFGetService) {
            *ppv = static_cast<IMFGetService*>(this);
        } else if (riid == KC_IID_IKsControl) {
            *ppv = static_cast<IKsControlLocal*>(this);
        } else {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override {
        long r = --refs_;
        if (r == 0) delete this;
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override {
        IMFMediaEventQueue* q;
        { std::lock_guard<std::mutex> g(lock_); q = queue_; if (!q) return MF_E_SHUTDOWN; q->AddRef(); }
        HRESULT hr = q->GetEvent(flags, ev);
        q->Release();
        return hr;
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->BeginGetEvent(cb, state) : MF_E_SHUTDOWN;
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->EndGetEvent(result, ev) : MF_E_SHUTDOWN;
    }
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override {
        std::lock_guard<std::mutex> g(lock_);
        return queue_ ? queue_->QueueEventParamVar(type, ext, status, value) : MF_E_SHUTDOWN;
    }

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* c) override {
        if (!c) return E_POINTER;
        *c = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!pd) return E_POINTER;
        if (!presentation_) return MF_E_SHUTDOWN;
        return presentation_->Clone(pd);
    }
    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPosition) override {
        if (!pd) return E_INVALIDARG;
        if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        BOOL selected = FALSE;
        IMFStreamDescriptor* sd = nullptr;
        HRESULT hr = pd->GetStreamDescriptorByIndex(0, &selected, &sd);
        IMFMediaType* type = nullptr;
        if (SUCCEEDED(hr)) {
            IMFMediaTypeHandler* handler = nullptr;
            if (SUCCEEDED(sd->GetMediaTypeHandler(&handler))) handler->GetCurrentMediaType(&type);
            SafeRelease(handler);
        }
        SafeRelease(sd);
        if (FAILED(hr)) return hr;
        if (selected) {
            hr = queue_->QueueEventParamUnk(started_ ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
                                            static_cast<IMFMediaStream2Local*>(stream_));
            if (SUCCEEDED(hr)) hr = stream_->Start(type);
        }
        SafeRelease(type);
        started_ = true;
        PROPVARIANT now;
        PropVariantInit(&now);
        now.vt = VT_I8;
        now.hVal.QuadPart = MFGetSystemTime();
        if (SUCCEEDED(hr)) hr = queue_->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &now);
        return hr;
    }
    STDMETHODIMP Stop() override {
        std::lock_guard<std::mutex> g(lock_);
        if (!queue_) return MF_E_SHUTDOWN;
        stream_->Stop();
        return queue_->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    }
    STDMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }
    STDMETHODIMP Shutdown() override {
        std::lock_guard<std::mutex> g(lock_);
        if (queue_) queue_->Shutdown();
        SafeRelease(queue_);
        if (stream_) {
            stream_->Shutdown();
            stream_->Release();
            stream_ = nullptr;
        }
        SafeRelease(presentation_);
        SafeRelease(attributes_);
        return S_OK;
    }

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** a) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!a) return E_POINTER;
        if (!attributes_) return MF_E_SHUTDOWN;
        *a = attributes_;
        attributes_->AddRef();
        return S_OK;
    }
    STDMETHODIMP GetStreamAttributes(DWORD id, IMFAttributes** a) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!a) return E_POINTER;
        if (!stream_) return MF_E_SHUTDOWN;
        if (id != 0) return MF_E_INVALIDSTREAMNUMBER;
        *a = stream_->Attributes();
        (*a)->AddRef();
        return S_OK;
    }
    STDMETHODIMP SetD3DManager(IUnknown*) override { return E_NOTIMPL; }

    // IMFGetService
    STDMETHODIMP GetService(REFGUID, REFIID, LPVOID*) override { return MF_E_UNSUPPORTED_SERVICE; }

    // IKsControl
    STDMETHODIMP KsProperty(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

private:
    ~MediaSource() {
        Shutdown();
        g_objects--;
    }

    std::atomic<long> refs_{1};
    std::mutex lock_;
    IMFAttributes* attributes_ = nullptr;
    IMFPresentationDescriptor* presentation_ = nullptr;
    IMFMediaEventQueue* queue_ = nullptr;
    MediaStream* stream_ = nullptr;
    bool started_ = false;
};

STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source) {
    std::lock_guard<std::mutex> g(lock_);
    if (!source) return E_POINTER;
    if (!parent_) return MF_E_SHUTDOWN;
    return parent_->QueryInterface(IID_IMFMediaSource, (void**)source);
}

// ---- IMFActivate（Frame Server が最初に作る） ----

class Activator final : public IMFActivate {
public:
    Activator() { g_objects++; }
    HRESULT Initialize() { return MFCreateAttributes(&attributes_, 1); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IMFAttributes || riid == IID_IMFActivate) {
            *ppv = static_cast<IMFActivate*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++refs_; }
    STDMETHODIMP_(ULONG) Release() override {
        long r = --refs_;
        if (r == 0) delete this;
        return r;
    }

    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override {
        std::lock_guard<std::mutex> g(lock_);
        if (!source_) {
            auto* s = new (std::nothrow) MediaSource();
            if (!s) return E_OUTOFMEMORY;
            HRESULT hr = s->Initialize();
            if (FAILED(hr)) { s->Release(); return hr; }
            source_ = s;
        }
        return source_->QueryInterface(riid, ppv);
    }
    STDMETHODIMP ShutdownObject() override {
        std::lock_guard<std::mutex> g(lock_);
        if (source_) { source_->Shutdown(); source_->Release(); source_ = nullptr; }
        return S_OK;
    }
    STDMETHODIMP DetachObject() override {
        std::lock_guard<std::mutex> g(lock_);
        if (source_) { source_->Release(); source_ = nullptr; }
        return S_OK;
    }

    // IMFAttributes（中の属性へそのまま渡す）
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return attributes_->GetItem(k, v); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return attributes_->GetItemType(k, t); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return attributes_->CompareItem(k, v, r); }
    STDMETHODIMP Compare(IMFAttributes* t, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return attributes_->Compare(t, m, r); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return attributes_->GetUINT32(k, v); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return attributes_->GetUINT64(k, v); }
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return attributes_->GetDouble(k, v); }
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return attributes_->GetGUID(k, v); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return attributes_->GetStringLength(k, l); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 s, UINT32* l) override { return attributes_->GetString(k, v, s, l); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return attributes_->GetAllocatedString(k, v, l); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return attributes_->GetBlobSize(k, s); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 s, UINT32* l) override { return attributes_->GetBlob(k, b, s, l); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return attributes_->GetAllocatedBlob(k, b, s); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* p) override { return attributes_->GetUnknown(k, r, p); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return attributes_->SetItem(k, v); }
    STDMETHODIMP DeleteItem(REFGUID k) override { return attributes_->DeleteItem(k); }
    STDMETHODIMP DeleteAllItems() override { return attributes_->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return attributes_->SetUINT32(k, v); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return attributes_->SetUINT64(k, v); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return attributes_->SetDouble(k, v); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return attributes_->SetGUID(k, v); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return attributes_->SetString(k, v); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 s) override { return attributes_->SetBlob(k, b, s); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* u) override { return attributes_->SetUnknown(k, u); }
    STDMETHODIMP LockStore() override { return attributes_->LockStore(); }
    STDMETHODIMP UnlockStore() override { return attributes_->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* c) override { return attributes_->GetCount(c); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return attributes_->GetItemByIndex(i, k, v); }
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return attributes_->CopyAllItems(d); }

private:
    ~Activator() {
        if (source_) { source_->Shutdown(); source_->Release(); }
        SafeRelease(attributes_);
        g_objects--;
    }
    std::atomic<long> refs_{1};
    std::mutex lock_;
    IMFAttributes* attributes_ = nullptr;
    MediaSource* source_ = nullptr;
};

// ---- クラスファクトリと DLL の入口 ----

class ClassFactory final : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* a = new (std::nothrow) Activator();
        if (!a) return E_OUTOFMEMORY;
        HRESULT hr = a->Initialize();
        if (SUCCEEDED(hr)) hr = a->QueryInterface(riid, ppv);
        a->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override {
        if (lock) g_locks++; else g_locks--;
        return S_OK;
    }
};
static ClassFactory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    if (clsid != CLSID_KeyCueCamera) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow() {
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}

static HRESULT WriteRegistration(const wchar_t* dllPath) {
    wchar_t key[128];
    wsprintfW(key, L"Software\\Classes\\CLSID\\%s\\InprocServer32", kClsidString);
    HKEY h;
    LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key, 0, nullptr, 0, KEY_WRITE, nullptr, &h, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    r = RegSetValueExW(h, nullptr, 0, REG_SZ, (const BYTE*)dllPath, (DWORD)((wcslen(dllPath) + 1) * sizeof(wchar_t)));
    const wchar_t both[] = L"Both";
    if (r == ERROR_SUCCESS) r = RegSetValueExW(h, L"ThreadingModel", 0, REG_SZ, (const BYTE*)both, sizeof(both));
    RegCloseKey(h);
    return HRESULT_FROM_WIN32(r);
}

// regsvr32 でも入れられるように（KeyCue は自分で登録する）
STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(g_module, path, MAX_PATH);
    return WriteRegistration(path);
}

STDAPI DllUnregisterServer() {
    wchar_t key[128];
    wsprintfW(key, L"Software\\Classes\\CLSID\\%s", kClsidString);
    LONG r = RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    return r == ERROR_SUCCESS || r == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(r);
}

// ---- KeyCue.exe から呼ぶ ----

/// 仮想カメラ「KeyCue」を作って始める（KeyCue を終えると消える）。camera は KeyCueCameraDestroy で片付ける
extern "C" __declspec(dllexport) HRESULT KeyCueCameraCreate(const wchar_t* friendlyName, void** camera) {
    if (!camera) return E_POINTER;
    *camera = nullptr;
    HMODULE lib = LoadLibraryW(L"mfsensorgroup.dll");
    if (!lib) return HRESULT_FROM_WIN32(GetLastError());
    auto create = (MFCreateVirtualCameraFn)(void*)GetProcAddress(lib, "MFCreateVirtualCamera");
    if (!create) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);  // Windows 10 など
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(hr)) return hr;
    IMFVirtualCameraLocal* cam = nullptr;
    hr = create(KC_MFVirtualCameraType_SoftwareCameraSource, KC_MFVirtualCameraLifetime_Session, KC_MFVirtualCameraAccess_CurrentUser,
                friendlyName ? friendlyName : L"KeyCue", kClsidString, nullptr, 0, &cam);
    if (SUCCEEDED(hr)) hr = cam->Start(nullptr);
    if (FAILED(hr)) {
        if (cam) { cam->Shutdown(); cam->Release(); }
        return hr;
    }
    *camera = cam;
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT KeyCueCameraDestroy(void* camera) {
    auto* cam = (IMFVirtualCameraLocal*)camera;
    if (!cam) return S_OK;
    cam->Stop();
    cam->Remove();
    cam->Shutdown();
    cam->Release();
    return S_OK;
}

extern "C" __declspec(dllexport) const wchar_t* KeyCueCameraClsid() { return kClsidString; }

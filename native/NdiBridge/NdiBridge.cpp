#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace {

constexpr int kFrameNone = 0;
constexpr int kFrameVideo = 1;
constexpr int kFrameError = 4;
constexpr int kFourCcBgra = ('B' | ('G' << 8) | ('R' << 16) | ('A' << 24));
constexpr int kFourCcBgrx = ('B' | ('G' << 8) | ('R' << 16) | ('X' << 24));

struct Source {
    const char* name;
    const char* url;
};

struct FindSettings {
    bool local;
    const char* groups;
    const char* extraIps;
};

struct VideoFrame {
    int xres;
    int yres;
    int fourCc;
    int frameRateN;
    int frameRateD;
    float aspect;
    int frameFormat;
    int64_t timecode;
    unsigned char* data;
    int stride;
    const char* metadata;
    int64_t timestamp;
};

struct RecvSettings {
    Source source;
    int colorFormat;
    int bandwidth;
    bool fields;
    const char* receiverName;
};

struct SendSettings {
    const char* name;
    const char* groups;
    bool clockVideo;
    bool clockAudio;
};

using Initialize = bool (*)();
using Destroy = void (*)();
using FindCreate = void* (*)(const FindSettings*);
using FindDestroy = void (*)(void*);
using FindSources = const Source* (*)(void*, uint32_t*);
using FindWait = bool (*)(void*, uint32_t);
using RecvCreate = void* (*)(const RecvSettings*);
using RecvDestroy = void (*)(void*);
using RecvCapture = int (*)(void*, VideoFrame*, void*, void*, uint32_t);
using RecvFreeVideo = void (*)(void*, const VideoFrame*);
using SendCreate = void* (*)(const SendSettings*);
using SendDestroy = void (*)(void*);
using SendVideo = void (*)(void*, const VideoFrame*);

struct Bridge {
    HMODULE module = nullptr;
    Initialize initialize = nullptr;
    Destroy destroy = nullptr;
    FindCreate findCreate = nullptr;
    FindDestroy findDestroy = nullptr;
    FindSources findSources = nullptr;
    FindWait findWait = nullptr;
    RecvCreate recvCreate = nullptr;
    RecvDestroy recvDestroy = nullptr;
    RecvCapture recvCapture = nullptr;
    RecvFreeVideo recvFreeVideo = nullptr;
    SendCreate sendCreate = nullptr;
    SendDestroy sendDestroy = nullptr;
    SendVideo sendVideo = nullptr;
    std::vector<std::string> names;
};

std::atomic<int> g_runtimeReferences{0};

template <typename T>
T Resolve(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

std::vector<std::wstring> RuntimeCandidates() {
    std::vector<std::wstring> candidates;
    wchar_t configured[32768] = {};
    DWORD length = GetEnvironmentVariableW(L"MOTARA_NDI_RUNTIME_DLL", configured, 32768);
    if (length > 0 && length < 32768) {
        candidates.emplace_back(configured, length);
        return candidates;
    }

    candidates.emplace_back(L"Processing.NDI.Lib.x64.dll");
    candidates.emplace_back(L"Processing.NDI.Lib.dll");

    wchar_t programFiles[32768] = {};
    length = GetEnvironmentVariableW(L"ProgramFiles", programFiles, 32768);
    if (length > 0 && length < 32768) {
        std::wstring root(programFiles, length);
        candidates.push_back(root + L"\\NDI\\NDI 6 Runtime\\v6\\Processing.NDI.Lib.x64.dll");
        candidates.push_back(root + L"\\NDI\\NDI 5 Runtime\\v5\\Processing.NDI.Lib.x64.dll");
    }

    wchar_t programFilesX86[32768] = {};
    length = GetEnvironmentVariableW(L"ProgramFiles(x86)", programFilesX86, 32768);
    if (length > 0 && length < 32768) {
        std::wstring root(programFilesX86, length);
        candidates.push_back(root + L"\\NDI\\NDI 6 Runtime\\v6\\Processing.NDI.Lib.x64.dll");
        candidates.push_back(root + L"\\NDI\\NDI 5 Runtime\\v5\\Processing.NDI.Lib.x64.dll");
    }

    return candidates;
}

HMODULE LoadRuntime() {
    for (const std::wstring& candidate : RuntimeCandidates()) {
        if (HMODULE module = LoadLibraryW(candidate.c_str())) {
            return module;
        }
    }
    return nullptr;
}

bool CopyUtf8(const std::string& value, char* output, int capacity) {
    if (!output || capacity <= 0) return false;
    size_t count = std::min(value.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(output, value.data(), count);
    output[count] = '\0';
    return true;
}

bool ResolveAll(Bridge* bridge) {
    bridge->initialize = Resolve<Initialize>(bridge->module, "NDIlib_initialize");
    bridge->destroy = Resolve<Destroy>(bridge->module, "NDIlib_destroy");
    bridge->findCreate = Resolve<FindCreate>(bridge->module, "NDIlib_find_create_v2");
    bridge->findDestroy = Resolve<FindDestroy>(bridge->module, "NDIlib_find_destroy");
    bridge->findSources = Resolve<FindSources>(bridge->module, "NDIlib_find_get_current_sources");
    bridge->findWait = Resolve<FindWait>(bridge->module, "NDIlib_find_wait_for_sources");
    bridge->recvCreate = Resolve<RecvCreate>(bridge->module, "NDIlib_recv_create_v3");
    bridge->recvDestroy = Resolve<RecvDestroy>(bridge->module, "NDIlib_recv_destroy");
    bridge->recvCapture = Resolve<RecvCapture>(bridge->module, "NDIlib_recv_capture_v3");
    bridge->recvFreeVideo = Resolve<RecvFreeVideo>(bridge->module, "NDIlib_recv_free_video_v2");
    bridge->sendCreate = Resolve<SendCreate>(bridge->module, "NDIlib_send_create");
    bridge->sendDestroy = Resolve<SendDestroy>(bridge->module, "NDIlib_send_destroy");
    bridge->sendVideo = Resolve<SendVideo>(bridge->module, "NDIlib_send_send_video_v2");
    return bridge->initialize && bridge->destroy && bridge->findCreate && bridge->findDestroy
        && bridge->findSources && bridge->findWait && bridge->recvCreate && bridge->recvDestroy
        && bridge->recvCapture && bridge->recvFreeVideo && bridge->sendCreate
        && bridge->sendDestroy && bridge->sendVideo;
}

} // namespace

extern "C" __declspec(dllexport) void* motara_ndi_create() {
    auto* bridge = new Bridge();
    bridge->module = LoadRuntime();
    if (!bridge->module || !ResolveAll(bridge)) {
        if (bridge->module) FreeLibrary(bridge->module);
        delete bridge;
        return nullptr;
    }
    if (g_runtimeReferences.fetch_add(1) == 0 && !bridge->initialize()) {
        g_runtimeReferences.fetch_sub(1);
        FreeLibrary(bridge->module);
        delete bridge;
        return nullptr;
    }
    return bridge;
}

extern "C" __declspec(dllexport) void motara_ndi_destroy(void* handle) {
    auto* bridge = static_cast<Bridge*>(handle);
    if (!bridge) return;
    if (g_runtimeReferences.fetch_sub(1) == 1) bridge->destroy();
    FreeLibrary(bridge->module);
    delete bridge;
}

extern "C" __declspec(dllexport) int motara_ndi_source_count(void* handle) {
    auto* bridge = static_cast<Bridge*>(handle);
    if (!bridge) return 0;
    FindSettings findSettings{true, nullptr, nullptr};
    void* finder = bridge->findCreate(&findSettings);
    if (!finder) return 0;
    bridge->findWait(finder, 250);
    uint32_t count = 0;
    const Source* sources = bridge->findSources(finder, &count);
    bridge->names.clear();
    for (uint32_t i = 0; i < count; ++i) bridge->names.emplace_back(sources[i].name ? sources[i].name : "");
    bridge->findDestroy(finder);
    return static_cast<int>(bridge->names.size());
}

extern "C" __declspec(dllexport) int motara_ndi_source_info(void* handle, int index, char* name, int capacity) {
    auto* bridge = static_cast<Bridge*>(handle);
    if (!bridge || index < 0 || index >= static_cast<int>(bridge->names.size())) return 0;
    return CopyUtf8(bridge->names[index], name, capacity) ? 1 : 0;
}

extern "C" __declspec(dllexport) void* motara_ndi_receiver_open(void* handle, const char* sourceName) {
    auto* bridge = static_cast<Bridge*>(handle);
    if (!bridge || !sourceName) return nullptr;
    FindSettings findSettings{true, nullptr, nullptr};
    void* finder = bridge->findCreate(&findSettings);
    if (!finder) return nullptr;
    bridge->findWait(finder, 250);
    uint32_t count = 0;
    const Source* sources = bridge->findSources(finder, &count);
    Source selected{};
    for (uint32_t i = 0; i < count; ++i) {
        if (sources[i].name && std::strcmp(sources[i].name, sourceName) == 0) { selected = sources[i]; break; }
    }
    RecvSettings recvSettings{selected, 0, 100, false, "Motara"};
    void* receiver = selected.name ? bridge->recvCreate(&recvSettings) : nullptr;
    bridge->findDestroy(finder);
    return receiver;
}

extern "C" __declspec(dllexport) int motara_ndi_receiver_receive(void* handle, void* receiver, unsigned char* pixels, int capacity, int* width, int* height, int* isNew) {
    auto* bridge = static_cast<Bridge*>(handle);
    if (!bridge || !receiver || !pixels || !width || !height || !isNew) return 0;
    VideoFrame frame{};
    int type = bridge->recvCapture(receiver, &frame, nullptr, nullptr, 0);
    if (type != kFrameVideo || !frame.data || frame.xres <= 0 || frame.yres <= 0) { *isNew = type == kFrameNone ? 0 : -1; return 0; }
    int required = frame.xres * frame.yres * 4;
    if (capacity < required || (frame.fourCc != kFourCcBgra && frame.fourCc != kFourCcBgrx) || frame.stride < frame.xres * 4) { bridge->recvFreeVideo(receiver, &frame); return -required; }
    for (int y = 0; y < frame.yres; ++y) {
        std::memcpy(pixels + y * frame.xres * 4, frame.data + y * frame.stride, frame.xres * 4);
        if (frame.fourCc == kFourCcBgrx) for (int x = 0; x < frame.xres; ++x) pixels[y * frame.xres * 4 + x * 4 + 3] = 255;
    }
    *width = frame.xres; *height = frame.yres; *isNew = 1;
    bridge->recvFreeVideo(receiver, &frame);
    return required;
}

extern "C" __declspec(dllexport) void motara_ndi_receiver_close(void* handle, void* receiver) { auto* bridge = static_cast<Bridge*>(handle); if (bridge && receiver) bridge->recvDestroy(receiver); }
extern "C" __declspec(dllexport) void* motara_ndi_sender_open(void* handle, const char* name) { auto* bridge = static_cast<Bridge*>(handle); SendSettings settings{name, nullptr, true, false}; return bridge && name ? bridge->sendCreate(&settings) : nullptr; }
extern "C" __declspec(dllexport) int motara_ndi_sender_send(void* handle, void* sender, const unsigned char* pixels, int width, int height, int stride) {
    auto* bridge = static_cast<Bridge*>(handle); if (!bridge || !sender || !pixels || width <= 0 || height <= 0 || stride < width * 4) return 0;
    VideoFrame frame{width, height, kFourCcBgra, 30000, 1001, 0, 1, INT64_MAX, const_cast<unsigned char*>(pixels), stride, nullptr, 0};
    bridge->sendVideo(sender, &frame); return 1;
}
extern "C" __declspec(dllexport) void motara_ndi_sender_close(void* handle, void* sender) { auto* bridge = static_cast<Bridge*>(handle); if (bridge && sender) bridge->sendDestroy(sender); }

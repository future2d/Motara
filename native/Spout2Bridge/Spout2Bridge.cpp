#include "SpoutDX.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace {

struct Bridge {
    spoutDX* api = nullptr;
    std::vector<unsigned char> receiveBuffer;
    unsigned int width = 0;
    unsigned int height = 0;
    bool receiverOpen = false;
    bool senderOpen = false;
};

bool CopyName(const std::string& value, char* destination, int capacity) {
    if (destination == nullptr || capacity <= 0) {
        return false;
    }

    const size_t count = std::min(value.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(destination, value.data(), count);
    destination[count] = '\0';
    return true;
}

} // namespace

extern "C" __declspec(dllexport) void* motara_spout2_create() {
    Bridge* bridge = new Bridge();
    bridge->api = new spoutDX();
    if (bridge->api == nullptr) {
        delete bridge;
        return nullptr;
    }

    return bridge;
}

extern "C" __declspec(dllexport) void motara_spout2_destroy(void* handle) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr) {
        return;
    }

    if (bridge->api != nullptr) {
        if (bridge->receiverOpen) {
            bridge->api->ReleaseReceiver();
        }
        if (bridge->senderOpen) {
            bridge->api->ReleaseSender();
        }
        delete bridge->api;
    }
    delete bridge;
}

extern "C" __declspec(dllexport) int motara_spout2_sender_count(void* handle) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr) {
        return 0;
    }

    return bridge->api->GetSenderCount();
}

extern "C" __declspec(dllexport) int motara_spout2_sender_info(
    void* handle,
    int index,
    char* name,
    int nameCapacity,
    unsigned int* width,
    unsigned int* height,
    double* fps) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr || index < 0
        || width == nullptr || height == nullptr || fps == nullptr) {
        return 0;
    }

    std::vector<std::string> senders = bridge->api->GetSenderList();
    if (index >= static_cast<int>(senders.size()) || !CopyName(senders[index], name, nameCapacity)) {
        return 0;
    }

    HANDLE shareHandle = nullptr;
    DWORD format = 0;
    unsigned int senderWidth = 0;
    unsigned int senderHeight = 0;
    if (!bridge->api->GetSenderInfo(senders[index].c_str(), senderWidth, senderHeight, shareHandle, format)
        || senderWidth == 0 || senderHeight == 0) {
        return 0;
    }

    *width = senderWidth;
    *height = senderHeight;
    *fps = 0.0;
    return 1;
}

extern "C" __declspec(dllexport) int motara_spout2_receiver_open(
    void* handle,
    const char* senderName,
    unsigned int* width,
    unsigned int* height) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr || senderName == nullptr
        || width == nullptr || height == nullptr) {
        return 0;
    }

    HANDLE shareHandle = nullptr;
    DWORD format = 0;
    unsigned int receiverWidth = 0;
    unsigned int receiverHeight = 0;
    if (!bridge->api->GetSenderInfo(senderName, receiverWidth, receiverHeight, shareHandle, format)
        || receiverWidth == 0 || receiverHeight == 0) {
        return 0;
    }

    bridge->api->SetReceiverName(senderName);
    bridge->width = receiverWidth;
    bridge->height = receiverHeight;
    bridge->receiveBuffer.resize(static_cast<size_t>(receiverWidth) * receiverHeight * 4);
    bridge->receiverOpen = true;
    *width = receiverWidth;
    *height = receiverHeight;
    return 1;
}

extern "C" __declspec(dllexport) int motara_spout2_receiver_receive(
    void* handle,
    unsigned char* pixels,
    int capacity,
    int* isNewFrame) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr || pixels == nullptr
        || capacity < 0 || bridge->receiveBuffer.empty()) {
        return 0;
    }

    // ReceiveImage intentionally leaves its update flag set until the
    // application acknowledges a sender/size change. Clear that flag before
    // the next receive so the DirectX staging path can copy pixels.
    bridge->api->IsUpdated();
    if (!bridge->api->ReceiveImage(
        bridge->receiveBuffer.data(), bridge->width, bridge->height, false, false)) {
        return 0;
    }

    const size_t bytes = bridge->receiveBuffer.size();
    if (static_cast<size_t>(capacity) < bytes) {
        return 0;
    }

    std::memcpy(pixels, bridge->receiveBuffer.data(), bytes);
    if (isNewFrame != nullptr) {
        *isNewFrame = bridge->api->IsFrameNew() ? 1 : 0;
    }
    return static_cast<int>(bytes);
}

extern "C" __declspec(dllexport) int motara_spout2_sender_open(
    void* handle,
    const char* senderName,
    unsigned int width,
    unsigned int height) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr || senderName == nullptr
        || width == 0 || height == 0) {
        return 0;
    }

    bridge->api->SetSenderName(senderName);
    bridge->api->SetSenderFormat(DXGI_FORMAT_B8G8R8A8_UNORM);
    bridge->receiveBuffer.assign(static_cast<size_t>(width) * height * 4, 0);
    if (!bridge->api->SendImage(bridge->receiveBuffer.data(), width, height, width * 4)) {
        bridge->receiveBuffer.clear();
        return 0;
    }

    bridge->width = width;
    bridge->height = height;
    bridge->senderOpen = true;
    return 1;
}

extern "C" __declspec(dllexport) int motara_spout2_sender_send(
    void* handle,
    const unsigned char* pixels,
    unsigned int width,
    unsigned int height) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge == nullptr || bridge->api == nullptr || pixels == nullptr) {
        return 0;
    }

    return bridge->api->SendImage(pixels, width, height, width * 4) ? 1 : 0;
}

extern "C" __declspec(dllexport) void motara_spout2_receiver_close(void* handle) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge != nullptr && bridge->api != nullptr) {
        bridge->api->ReleaseReceiver();
        bridge->receiverOpen = false;
    }
}

extern "C" __declspec(dllexport) void motara_spout2_sender_close(void* handle) {
    Bridge* bridge = static_cast<Bridge*>(handle);
    if (bridge != nullptr && bridge->api != nullptr) {
        bridge->api->ReleaseSender();
        bridge->senderOpen = false;
    }
}

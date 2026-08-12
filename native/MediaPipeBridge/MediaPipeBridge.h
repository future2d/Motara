#pragma once

#ifdef _WIN32
#define MOTARA_MEDIAPIPE_EXPORT __declspec(dllexport)
#else
#define MOTARA_MEDIAPIPE_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MotaraMediaPipeBlendshape {
  int index;
  float score;
} MotaraMediaPipeBlendshape;

typedef struct MotaraMediaPipeFrame {
  MotaraMediaPipeBlendshape* blendshapes;
  int blendshape_capacity;
  int blendshape_count;
  int face_detected;
} MotaraMediaPipeFrame;

MOTARA_MEDIAPIPE_EXPORT void* motara_mp_create(
    const char* model_path,
    char** error_message);

MOTARA_MEDIAPIPE_EXPORT int motara_mp_process_rgba(
    void* handle,
    const unsigned char* rgba,
    int width,
    int height,
    long long timestamp_milliseconds,
    MotaraMediaPipeFrame* output,
    char** error_message);

MOTARA_MEDIAPIPE_EXPORT void motara_mp_free_error(char* error_message);

MOTARA_MEDIAPIPE_EXPORT int motara_mp_close(
    void* handle,
    char** error_message);

#ifdef __cplusplus
}
#endif

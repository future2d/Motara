#include "MediaPipeBridge.h"

#include <cstdlib>
#include <cstring>

#include "mediapipe/tasks/c/vision/core/common.h"
#include "mediapipe/tasks/c/vision/face_landmarker/face_landmarker.h"

namespace {

struct BridgeHandle {
  void* landmarker = nullptr;
};

void SetError(char** target, const char* message) {
  if (target == nullptr) {
    return;
  }

  const size_t length = std::strlen(message);
  *target = static_cast<char*>(std::malloc(length + 1));
  if (*target != nullptr) {
    std::memcpy(*target, message, length + 1);
  }
}

}  // namespace

void* motara_mp_create(const char* model_path, char** error_message) {
  if (model_path == nullptr || *model_path == '\0') {
    SetError(error_message, "A MediaPipe model path is required.");
    return nullptr;
  }

  FaceLandmarkerOptions options{};
  options.base_options.model_asset_path = model_path;
  options.running_mode = VIDEO;
  options.num_faces = 1;
  options.output_face_blendshapes = true;
  options.output_facial_transformation_matrixes = false;

  BridgeHandle* handle = new BridgeHandle();
  handle->landmarker = face_landmarker_create(&options, error_message);
  if (handle->landmarker == nullptr) {
    delete handle;
    return nullptr;
  }

  return handle;
}

int motara_mp_process_rgba(
    void* raw_handle,
    const unsigned char* rgba,
    int width,
    int height,
    long long timestamp_milliseconds,
    MotaraMediaPipeFrame* output,
    char** error_message) {
  if (raw_handle == nullptr || rgba == nullptr || output == nullptr
      || width <= 0 || height <= 0 || output->blendshapes == nullptr
      || output->blendshape_capacity < 0) {
    SetError(error_message, "Invalid MediaPipe frame arguments.");
    return 1;
  }

  BridgeHandle* handle = static_cast<BridgeHandle*>(raw_handle);
  MpImage image{};
  image.type = MpImage::IMAGE_FRAME;
  image.image_frame.format = SRGBA;
  image.image_frame.image_buffer = rgba;
  image.image_frame.width = width;
  image.image_frame.height = height;

  FaceLandmarkerResult result{};
  const int status = face_landmarker_detect_for_video(
      handle->landmarker,
      &image,
      timestamp_milliseconds,
      &result,
      error_message);
  if (status != 0) {
    return status;
  }

  output->blendshape_count = 0;
  output->face_detected = result.face_landmarks_count > 0 ? 1 : 0;
  if (result.face_blendshapes_count > 0
      && result.face_blendshapes != nullptr
      && result.face_blendshapes[0].categories != nullptr) {
    const int count = static_cast<int>(result.face_blendshapes[0].categories_count);
    const int copy_count = count < output->blendshape_capacity
        ? count
        : output->blendshape_capacity;
    for (int index = 0; index < copy_count; ++index) {
      output->blendshapes[index].index =
          result.face_blendshapes[0].categories[index].index;
      output->blendshapes[index].score =
          result.face_blendshapes[0].categories[index].score;
    }
    output->blendshape_count = copy_count;
  }

  face_landmarker_close_result(&result);
  return 0;
}

void motara_mp_free_error(char* error_message) {
  std::free(error_message);
}

int motara_mp_close(void* raw_handle, char** error_message) {
  if (raw_handle == nullptr) {
    return 0;
  }

  BridgeHandle* handle = static_cast<BridgeHandle*>(raw_handle);
  const int status = face_landmarker_close(handle->landmarker, error_message);
  delete handle;
  return status;
}

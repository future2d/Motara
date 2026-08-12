cmake_minimum_required(VERSION 3.20)
project(MotaraSpout2Bridge LANGUAGES CXX)

if (NOT WIN32)
    message(FATAL_ERROR "Motara Spout2 bridge is Windows-only")
endif()

if (NOT DEFINED SPOUT_SDK_ROOT)
    message(FATAL_ERROR "SPOUT_SDK_ROOT must point to the Spout2 source tree")
endif()

set(SPOUT_LIBRARY_DIR "${SPOUT_SDK_ROOT}/SPOUTSDK/SpoutLibrary")
set(SPOUT_GL_DIR "${SPOUT_SDK_ROOT}/SPOUTSDK/SpoutGL")
set(SPOUT_DX_DIR "${SPOUT_SDK_ROOT}/SPOUTSDK/SpoutDirectX/SpoutDX")

add_library(Motara.Spout2.Native SHARED Spout2Bridge.cpp)
target_include_directories(Motara.Spout2.Native PRIVATE "${SPOUT_DX_DIR}" "${SPOUT_GL_DIR}")
target_link_libraries(Motara.Spout2.Native PRIVATE
    "${CMAKE_BINARY_DIR}/spout-lib/libSpoutDX_static.a"
    "${CMAKE_BINARY_DIR}/spout-lib/libSpout_static.a"
    opengl32
    d3d11
    dxgi
    winmm
    version)
target_compile_features(Motara.Spout2.Native PRIVATE cxx_std_17)
if (MINGW)
    target_link_options(Motara.Spout2.Native PRIVATE -static-libgcc -static-libstdc++ -static)
endif()
set_target_properties(Motara.Spout2.Native PROPERTIES OUTPUT_NAME "Motara.Spout2.Native" PREFIX "")

cmake_minimum_required(VERSION 3.20)
project(MotaraNdiBridge LANGUAGES CXX)

if (NOT WIN32)
    message(FATAL_ERROR "Motara NDI bridge is Windows-only")
endif()

add_library(Motara.Ndi.Native SHARED NdiBridge.cpp)
target_compile_features(Motara.Ndi.Native PRIVATE cxx_std_17)
target_link_libraries(Motara.Ndi.Native PRIVATE ole32)
if (MINGW)
    target_link_options(Motara.Ndi.Native PRIVATE -static-libgcc -static-libstdc++ -static)
endif()
set_target_properties(Motara.Ndi.Native PROPERTIES OUTPUT_NAME "Motara.Ndi.Native" PREFIX "")

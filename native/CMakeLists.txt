# Build whisper.dll for Taptalk (Windows x64, AVX-512/AVX2)
# Usage (from whisper.cpp source root):
#   cmake -B build -G "Visual Studio 17 2022" -A x64 ^
#     -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON ^
#     -DGGML_AVX512=ON -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=OFF
#   cmake --build build --config Release --target whisper
#
# This file is included by the GitHub Actions workflow automatically.

cmake_minimum_required(VERSION 3.20)
project(taptalk_whisper C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Whisper.cpp is added as a subdirectory. The path is passed via WHISPER_SRC_DIR.
if(NOT WHISPER_SRC_DIR)
    set(WHISPER_SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/whisper.cpp")
endif()

# Force x64 only
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Taptalk requires a 64-bit build (x64)")
endif()

add_subdirectory(${WHISPER_SRC_DIR} whisper-build)

# Ensure whisper target exists as shared lib
set_target_properties(whisper PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/$<CONFIG>"
)

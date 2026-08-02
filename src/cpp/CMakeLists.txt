cmake_minimum_required(VERSION 3.16)
project(rc_core C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ---- MemoryModulePP (Phase 8: memory loading, inspired by zzrat) ----------
add_definitions(-DMEMORYLOAD)
message(STATUS "MemoryModulePP enabled")

# ---- ffmpeg (provided by bootstrap.py via FFMPEG_ROOT) -----------------
if(NOT FFMPEG_ROOT AND DEFINED ENV{FFMPEG_ROOT})
  set(FFMPEG_ROOT $ENV{FFMPEG_ROOT})
endif()

find_path(AVCODEC_INCLUDE NAMES libavcodec/avcodec.h PATHS ${FFMPEG_ROOT}/include REQUIRED)
find_path(AVUTIL_INCLUDE  NAMES libavutil/avutil.h   PATHS ${FFMPEG_ROOT}/include REQUIRED)
find_path(SWSCALE_INCLUDE NAMES libswscale/swscale.h PATHS ${FFMPEG_ROOT}/include REQUIRED)
find_path(AVFORMAT_INCLUDE NAMES libavformat/avformat.h PATHS ${FFMPEG_ROOT}/include REQUIRED)
find_path(SWRESAMPLE_INCLUDE NAMES libswresample/swresample.h PATHS ${FFMPEG_ROOT}/include REQUIRED)
find_path(OPUS_INCLUDE NAMES opus/opus.h PATHS ${FFMPEG_ROOT}/include REQUIRED)

find_library(AVCODEC_LIB NAMES avcodec PATHS ${FFMPEG_ROOT}/lib REQUIRED)
find_library(AVUTIL_LIB  NAMES avutil  PATHS ${FFMPEG_ROOT}/lib REQUIRED)
find_library(SWSCALE_LIB NAMES swscale PATHS ${FFMPEG_ROOT}/lib REQUIRED)
find_library(AVFORMAT_LIB NAMES avformat PATHS ${FFMPEG_ROOT}/lib REQUIRED)
find_library(SWRESAMPLE_LIB NAMES swresample PATHS ${FFMPEG_ROOT}/lib REQUIRED)
find_library(OPUS_LIB NAMES opus PATHS ${FFMPEG_ROOT}/lib REQUIRED)

# ---- Static FFmpeg (Phase 12: slim deploy agent) ------------------------
option(STATIC_FFMPEG "Link FFmpeg libraries statically (no external .dll needed)" OFF)

if(STATIC_FFMPEG)
  set(CMAKE_FIND_LIBRARY_SUFFIXES .a ${CMAKE_FIND_LIBRARY_SUFFIXES})
  # Re-find with static preference
  find_library(AVCODEC_LIB NAMES avcodec PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  find_library(AVUTIL_LIB  NAMES avutil  PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  find_library(SWSCALE_LIB NAMES swscale PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  find_library(AVFORMAT_LIB NAMES avformat PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  find_library(SWRESAMPLE_LIB NAMES swresample PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  find_library(OPUS_LIB NAMES opus PATHS ${FFMPEG_ROOT}/lib REQUIRED NO_DEFAULT_PATH)
  # Required codec libs (must be listed after ffmpeg in linker order)
  find_library(X264_LIB NAMES x264 PATHS ${FFMPEG_ROOT}/lib)
  find_library(VPX_LIB NAMES vpx PATHS ${FFMPEG_ROOT}/lib)
  find_library(AAC_LIB NAMES fdk-aac PATHS ${FFMPEG_ROOT}/lib)
  find_library(MP3LAME_LIB NAMES mp3lame PATHS ${FFMPEG_ROOT}/lib)
  find_library(Z_LIB NAMES z PATHS ${FFMPEG_ROOT}/lib)
  message(STATUS "Static FFmpeg: x264=${X264_LIB} vpx=${VPX_LIB}")
endif()

add_definitions(-DRC_CORE_EXPORTS)

add_library(rc_core SHARED
  rc_core.cpp
  capture.cpp
  encoder.cpp
  decoder.cpp
  input.cpp
  system.cpp
  recorder.cpp
  audio.cpp
  memload.cpp
  MemoryModulePP.c
)

target_include_directories(rc_core PRIVATE
  ${AVCODEC_INCLUDE} ${AVUTIL_INCLUDE} ${SWSCALE_INCLUDE} ${AVFORMAT_INCLUDE}
  ${SWRESAMPLE_INCLUDE} ${OPUS_INCLUDE})
target_link_libraries(rc_core
  ${AVFORMAT_LIB} ${AVCODEC_LIB} ${AVUTIL_LIB} ${SWSCALE_LIB}
  ${SWRESAMPLE_LIB} ${OPUS_LIB}
  dxgi d3d11 user32 ole32 powrprof)
if(STATIC_FFMPEG)
  target_link_libraries(rc_core ${X264_LIB} ${VPX_LIB} ${AAC_LIB} ${MP3LAME_LIB} ${Z_LIB})
  target_link_options(rc_core PRIVATE -static)
endif()

# MinGW: 静态链接 gcc 运行库，使 rc_core.dll 自包含（无需再分发 MinGW 的 DLL）。
if(MINGW)
  target_link_options(rc_core PRIVATE -static-libgcc -static-libstdc++)
endif()

# Place the .dll next to this project's build output for easy pickup.
# PREFIX "" -> MinGW would otherwise emit "librc_core.dll"; C# P/Invoke wants "rc_core.dll".
set_target_properties(rc_core PROPERTIES
  PREFIX ""
  RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})

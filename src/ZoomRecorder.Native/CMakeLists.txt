cmake_minimum_required(VERSION 3.28)
project(ZoomRecorderNative LANGUAGES CXX)
enable_testing()

add_library(ZoomRecorder.Native SHARED src/api.cpp)
target_compile_features(ZoomRecorder.Native PRIVATE cxx_std_20)
target_include_directories(ZoomRecorder.Native PUBLIC include)
target_compile_definitions(ZoomRecorder.Native PRIVATE UNICODE _UNICODE)

add_executable(ZoomRecorder.Native.Tests ../../tests/ZoomRecorder.Native.Tests/api_tests.cpp)
target_compile_features(ZoomRecorder.Native.Tests PRIVATE cxx_std_20)
target_link_libraries(ZoomRecorder.Native.Tests PRIVATE ZoomRecorder.Native)
add_test(NAME native_api COMMAND ZoomRecorder.Native.Tests)

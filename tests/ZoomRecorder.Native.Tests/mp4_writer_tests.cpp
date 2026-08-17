#include "mp4_writer.h"
#include <windows.h>
#include <string>

bool run_mp4_writer_tests() {
  wchar_t temporary[MAX_PATH]{};
  if (!GetTempPathW(MAX_PATH, temporary)) return false;
  const auto final_path = std::wstring(temporary) + L"zoom-recorder-writer-test.mp4";
  DeleteFileW(final_path.c_str());
  DeleteFileW((final_path + L".partial").c_str());
  DeleteFileW((final_path + L".partial.mp4").c_str());
  Mp4Writer writer;
  const auto opened = writer.open(final_path, 640, 360, 30);
  writer.finalize();
  DeleteFileW(final_path.c_str());
  DeleteFileW((final_path + L".partial").c_str());
  DeleteFileW((final_path + L".partial.mp4").c_str());
  return opened;
}

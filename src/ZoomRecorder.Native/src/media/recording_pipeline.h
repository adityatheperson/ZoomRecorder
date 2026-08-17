#pragma once

#include <windows.h>
#include <memory>
#include <string>
#include <functional>

class RecordingPipelineImpl;

class RecordingPipeline {
 public:
  using HealthCallback = std::function<void(bool, const char*)>;
  explicit RecordingPipeline(HealthCallback health);
  ~RecordingPipeline();
  bool start(HWND meeting_host, const std::wstring& output_path);
  bool stop_and_finalize();
  bool is_ready() const;
 private:
  std::unique_ptr<RecordingPipelineImpl> impl_;
};

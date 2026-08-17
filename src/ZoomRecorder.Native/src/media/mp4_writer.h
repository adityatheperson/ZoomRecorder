#pragma once

#include <d3d11.h>
#include <cstdint>
#include <memory>
#include <span>
#include <string>

class Mp4WriterImpl;

class Mp4Writer {
 public:
  Mp4Writer();
  ~Mp4Writer();
  bool open(const std::wstring& final_path, unsigned width, unsigned height, unsigned frame_rate = 30);
  bool write_video(ID3D11Texture2D* texture, std::int64_t timestamp_100ns);
  bool write_audio(std::span<const float> interleaved_stereo, std::int64_t timestamp_100ns);
  bool finalize();
  bool is_open() const;
  ID3D11Device* device() const;
  const std::wstring& final_path() const;
 private:
  std::unique_ptr<Mp4WriterImpl> impl_;
};

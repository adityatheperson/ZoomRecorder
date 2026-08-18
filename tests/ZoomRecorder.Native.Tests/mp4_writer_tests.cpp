#include "mp4_writer.h"
#include <d3d11.h>
#include <wrl/client.h>
#include <windows.h>
#include <string>
#include <cstdio>
#include <vector>

bool run_mp4_writer_tests() {
  wchar_t temporary[MAX_PATH]{};
  if (!GetTempPathW(MAX_PATH, temporary)) return false;
  const auto final_path = std::wstring(temporary) + L"zoom-recorder-writer-test.mp4";
  DeleteFileW(final_path.c_str());
  DeleteFileW((final_path + L".partial").c_str());
  DeleteFileW((final_path + L".partial.mp4").c_str());
  Mp4Writer writer;
  auto* device = writer.device();
  const auto device_created = device != nullptr;
  const auto opened = device_created && writer.open(final_path, 640, 360, 30);
  D3D11_TEXTURE2D_DESC description{};
  description.Width = 640; description.Height = 360; description.MipLevels = 1; description.ArraySize = 1;
  description.Format = DXGI_FORMAT_B8G8R8A8_UNORM; description.SampleDesc.Count = 1;
  description.Usage = D3D11_USAGE_DEFAULT; description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
  Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
  const auto texture_created = device_created && SUCCEEDED(device->CreateTexture2D(&description, nullptr, &texture));
  const std::vector<float> silent_audio(960 * 2);
  const auto early_audio_accepted = writer.write_audio(silent_audio, 0);
  const auto video_written = texture_created && writer.write_video(texture.Get(), 0);
  const auto audio_written = writer.write_audio(silent_audio, 200000);
  if (!opened || !device_created || !texture_created || !early_audio_accepted || !video_written || !audio_written)
    std::fprintf(stderr, "mp4 path: open=%d device=%d texture=%d early_audio=%d video=%d audio=%d audio_hresult=0x%08lX\n",
      opened, device_created, texture_created, early_audio_accepted, video_written, audio_written,
      static_cast<unsigned long>(writer.last_error()));
  writer.finalize();
  DeleteFileW(final_path.c_str());
  DeleteFileW((final_path + L".partial").c_str());
  DeleteFileW((final_path + L".partial.mp4").c_str());
  return opened && early_audio_accepted && video_written && audio_written;
}

#include "video_frame_normalizer.h"
#include <d3d11.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

static bool normalizes_to_full_hd(ID3D11Device* device, VideoFrameNormalizer& normalizer,
                                  UINT source_width, UINT source_height) {
  D3D11_TEXTURE2D_DESC description{};
  description.Width = source_width; description.Height = source_height;
  description.MipLevels = 1; description.ArraySize = 1;
  description.Format = DXGI_FORMAT_B8G8R8A8_UNORM; description.SampleDesc.Count = 1;
  description.Usage = D3D11_USAGE_DEFAULT; description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
  ComPtr<ID3D11Texture2D> source;
  if (FAILED(device->CreateTexture2D(&description, nullptr, &source))) return false;
  auto* output = normalizer.normalize(device, source.Get(), 1920, 1080);
  if (!output) return false;
  D3D11_TEXTURE2D_DESC output_description{};
  output->GetDesc(&output_description);
  return output_description.Width == 1920 && output_description.Height == 1080;
}

bool run_video_frame_normalizer_tests() {
  ComPtr<ID3D11Device> device;
  if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
      D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
      nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, nullptr))) return false;
  VideoFrameNormalizer normalizer;
  return normalizes_to_full_hd(device.Get(), normalizer, 800, 600) &&
         normalizes_to_full_hd(device.Get(), normalizer, 1600, 900);
}

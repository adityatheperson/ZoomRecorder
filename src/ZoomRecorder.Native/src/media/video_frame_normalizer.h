#pragma once

#include <d3d11.h>
#include <wrl/client.h>

class VideoFrameNormalizer {
 public:
  ID3D11Texture2D* normalize(ID3D11Device* device, ID3D11Texture2D* source, UINT width, UINT height);

 private:
  bool configure(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& source, UINT width, UINT height);
  Microsoft::WRL::ComPtr<ID3D11Texture2D> output_;
  Microsoft::WRL::ComPtr<ID3D11VideoDevice> video_device_;
  Microsoft::WRL::ComPtr<ID3D11VideoContext> video_context_;
  Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
  Microsoft::WRL::ComPtr<ID3D11VideoProcessor> processor_;
  Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> output_view_;
  UINT source_width_{};
  UINT source_height_{};
  UINT output_width_{};
  UINT output_height_{};
};

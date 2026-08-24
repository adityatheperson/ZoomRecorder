#include "video_frame_normalizer.h"
#include "aspect_fit.h"

using Microsoft::WRL::ComPtr;

bool VideoFrameNormalizer::configure(ID3D11Device* device, const D3D11_TEXTURE2D_DESC& source, UINT width, UINT height) {
  output_.Reset(); input_.Reset(); output_view_.Reset(); processor_.Reset(); enumerator_.Reset(); context_.Reset(); video_context_.Reset(); video_device_.Reset();
  if (!device || !source.Width || !source.Height || !width || !height) return false;
  if (FAILED(device->QueryInterface(IID_PPV_ARGS(&video_device_)))) return false;
  device->GetImmediateContext(&context_);
  if (!context_ || FAILED(context_.As(&video_context_))) return false;
  D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
  content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
  content.InputWidth = source.Width; content.InputHeight = source.Height;
  content.OutputWidth = width; content.OutputHeight = height;
  content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
  if (FAILED(video_device_->CreateVideoProcessorEnumerator(&content, &enumerator_)) ||
      FAILED(video_device_->CreateVideoProcessor(enumerator_.Get(), 0, &processor_))) return false;
  auto input_texture_description = source;
  input_texture_description.MipLevels = 1; input_texture_description.ArraySize = 1;
  input_texture_description.Usage = D3D11_USAGE_DEFAULT;
  input_texture_description.BindFlags = 0;
  input_texture_description.CPUAccessFlags = 0; input_texture_description.MiscFlags = 0;
  if (FAILED(device->CreateTexture2D(&input_texture_description, nullptr, &input_))) return false;
  auto output_description = source;
  output_description.Width = width; output_description.Height = height;
  output_description.MipLevels = 1; output_description.ArraySize = 1;
  output_description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
  output_description.Usage = D3D11_USAGE_DEFAULT;
  output_description.CPUAccessFlags = 0; output_description.MiscFlags = 0;
  if (FAILED(device->CreateTexture2D(&output_description, nullptr, &output_))) return false;
  D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC view{};
  view.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
  if (FAILED(video_device_->CreateVideoProcessorOutputView(output_.Get(), enumerator_.Get(), &view, &output_view_))) return false;
  source_width_ = source.Width; source_height_ = source.Height;
  output_width_ = width; output_height_ = height;
  return true;
}

ID3D11Texture2D* VideoFrameNormalizer::normalize(ID3D11Device* device, ID3D11Texture2D* source, UINT width, UINT height) {
  if (!device || !source || !width || !height) return nullptr;
  D3D11_TEXTURE2D_DESC description{};
  source->GetDesc(&description);
  if (description.Width == width && description.Height == height) return source;
  if (!output_ || description.Width != source_width_ || description.Height != source_height_ ||
      width != output_width_ || height != output_height_) {
    if (!configure(device, description, width, height)) return nullptr;
  }
  D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC input_description{};
  input_description.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
  ComPtr<ID3D11VideoProcessorInputView> input;
  context_->CopyResource(input_.Get(), source);
  if (FAILED(video_device_->CreateVideoProcessorInputView(input_.Get(), enumerator_.Get(), &input_description, &input))) return nullptr;
  const auto fit = calculate_aspect_fit(description.Width, description.Height, width, height);
  if (!fit.width || !fit.height) return nullptr;
  RECT source_rect{0, 0, static_cast<LONG>(description.Width), static_cast<LONG>(description.Height)};
  RECT destination{static_cast<LONG>(fit.left), static_cast<LONG>(fit.top),
                   static_cast<LONG>(fit.left + fit.width), static_cast<LONG>(fit.top + fit.height)};
  RECT target{0, 0, static_cast<LONG>(width), static_cast<LONG>(height)};
  D3D11_VIDEO_COLOR black{}; black.RGBA.A = 1.0f;
  video_context_->VideoProcessorSetOutputBackgroundColor(processor_.Get(), FALSE, &black);
  video_context_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &target);
  video_context_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &source_rect);
  video_context_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &destination);
  D3D11_VIDEO_PROCESSOR_STREAM stream{};
  stream.Enable = TRUE; stream.pInputSurface = input.Get();
  return SUCCEEDED(video_context_->VideoProcessorBlt(processor_.Get(), output_view_.Get(), 0, 1, &stream)) ? output_.Get() : nullptr;
}

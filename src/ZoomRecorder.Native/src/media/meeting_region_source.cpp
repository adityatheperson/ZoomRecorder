#include "meeting_region_source.h"
#include "capture_crop.h"

#include <d3d11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>
#include <atomic>
#include <chrono>
#include <cstdio>

using namespace winrt;
namespace capture = winrt::Windows::Graphics::Capture;
namespace directx = winrt::Windows::Graphics::DirectX;
namespace direct3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {
direct3d::IDirect3DDevice make_device(ID3D11Device* native_device) {
  com_ptr<ID3D11Device> d3d;
  d3d.copy_from(native_device);
  auto dxgi = d3d.as<IDXGIDevice>();
  com_ptr<::IInspectable> inspectable;
  check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
  return inspectable.as<direct3d::IDirect3DDevice>();
}

capture::GraphicsCaptureItem item_for_window(HWND window) {
  auto interop = get_activation_factory<capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
  capture::GraphicsCaptureItem item{nullptr};
  check_hresult(interop->CreateForWindow(window, guid_of<capture::GraphicsCaptureItem>(), put_abi(item)));
  return item;
}
}

class MeetingRegionSourceImpl {
 public:
  MeetingRegionSourceImpl(HWND target, ID3D11Device* device, MeetingRegionSource::FrameCallback frame,
      MeetingRegionSource::HealthCallback health, MeetingRegionSource::EndedCallback ended)
      : target_(target), frame_(std::move(frame)), health_(std::move(health)), ended_(std::move(ended)) { native_device_.copy_from(device); }

  bool start() {
    if (!IsWindow(target_)) { health_(false, "Zoom meeting window is unavailable"); return false; }
    capture_window_ = GetAncestor(target_, GA_ROOT);
    if (!capture_window_) { health_(false, "Zoom meeting window is unavailable"); return false; }
    if (!capture::GraphicsCaptureSession::IsSupported()) { health_(false, "Windows Graphics Capture is not supported or is disabled"); return false; }
    try {
      // WinUI invokes us on its existing STA thread. Reinitializing it as MTA
      // fails with RPC_E_CHANGED_MODE; Windows Graphics Capture supports the
      // existing apartment and CreateFreeThreaded handles frame delivery.
      device_ = make_device(native_device_.get());
      item_ = item_for_window(capture_window_);
      closed_token_ = item_.Closed([this](auto const&, auto const&) {
        if (!end_notified_.exchange(true)) ended_();
      });
      pool_ = capture::Direct3D11CaptureFramePool::CreateFreeThreaded(device_, directx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, item_.Size());
      frame_token_ = pool_.FrameArrived([this](auto const& sender, auto const&) {
        if (auto frame = sender.TryGetNextFrame()) {
          auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
          com_ptr<ID3D11Texture2D> texture;
          if (SUCCEEDED(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()))) {
            D3D11_TEXTURE2D_DESC source_description{};
            texture->GetDesc(&source_description);
            RECT target_bounds{}, capture_bounds{};
            CaptureCrop crop{};
            if (!GetWindowRect(target_, &target_bounds) || !GetWindowRect(capture_window_, &capture_bounds) ||
                !calculate_capture_crop(target_bounds, capture_bounds, source_description.Width, source_description.Height, crop)) {
              health_(false, "Meeting area is outside the capturable app window");
              return;
            }
            if (!cropped_ || crop.width != crop_width_ || crop.height != crop_height_) {
              D3D11_TEXTURE2D_DESC cropped_description = source_description;
              cropped_description.Width = crop.width;
              cropped_description.Height = crop.height;
              cropped_description.MipLevels = 1;
              cropped_description.ArraySize = 1;
              cropped_description.Usage = D3D11_USAGE_DEFAULT;
              cropped_description.CPUAccessFlags = 0;
              cropped_description.MiscFlags = 0;
              com_ptr<ID3D11Device> native_device;
              texture->GetDevice(native_device.put());
              if (FAILED(native_device->CreateTexture2D(&cropped_description, nullptr, cropped_.put()))) {
                health_(false, "Meeting video crop texture could not be created");
                return;
              }
              native_device->GetImmediateContext(context_.put());
              crop_width_ = crop.width;
              crop_height_ = crop.height;
            }
            const D3D11_BOX source_box{crop.left, crop.top, 0, crop.left + crop.width, crop.top + crop.height, 1};
            context_->CopySubresourceRegion(cropped_.get(), 0, 0, 0, 0, texture.get(), 0, &source_box);
            const auto now = std::chrono::steady_clock::now().time_since_epoch();
            frame_(cropped_.get(), std::chrono::duration_cast<std::chrono::nanoseconds>(now).count() / 100);
          }
          if (!ready_.exchange(true)) health_(true, "Meeting video ready");
        }
      });
      session_ = pool_.CreateCaptureSession(item_);
      session_.IsCursorCaptureEnabled(false);
      session_.StartCapture();
      return true;
    } catch (winrt::hresult_error const& error) {
      char message[96]{};
      std::snprintf(message, sizeof(message), "Windows Graphics Capture could not start (HRESULT 0x%08lX)",
                    static_cast<unsigned long>(error.code()));
      health_(false, message);
      stop();
      return false;
    } catch (...) {
      health_(false, "Windows Graphics Capture could not start (unknown Windows error)");
      stop();
      return false;
    }
  }

  void stop() {
    if (item_ && closed_token_.value) item_.Closed(closed_token_);
    if (pool_) pool_.FrameArrived(frame_token_);
    if (session_) session_.Close();
    if (pool_) pool_.Close();
    session_ = nullptr; pool_ = nullptr; item_ = nullptr; device_ = nullptr; cropped_ = nullptr; context_ = nullptr;
    crop_width_ = crop_height_ = 0; capture_window_ = nullptr; ready_ = false;
  }
  bool is_ready() const { return ready_; }

 private:
  HWND target_{};
  HWND capture_window_{};
  com_ptr<ID3D11Device> native_device_;
  MeetingRegionSource::FrameCallback frame_;
  MeetingRegionSource::HealthCallback health_;
  MeetingRegionSource::EndedCallback ended_;
  std::atomic_bool ready_{};
  std::atomic_bool end_notified_{};
  direct3d::IDirect3DDevice device_{nullptr};
  capture::GraphicsCaptureItem item_{nullptr};
  capture::Direct3D11CaptureFramePool pool_{nullptr};
  capture::GraphicsCaptureSession session_{nullptr};
  com_ptr<ID3D11Texture2D> cropped_;
  com_ptr<ID3D11DeviceContext> context_;
  UINT crop_width_{};
  UINT crop_height_{};
  event_token frame_token_{};
  event_token closed_token_{};
};

MeetingRegionSource::MeetingRegionSource(HWND target, ID3D11Device* device, FrameCallback frame, HealthCallback health, EndedCallback ended)
    : impl_(std::make_unique<MeetingRegionSourceImpl>(target, device, std::move(frame), std::move(health), std::move(ended))) {}
MeetingRegionSource::~MeetingRegionSource() { impl_->stop(); }
bool MeetingRegionSource::start() { return impl_->start(); }
void MeetingRegionSource::stop() { impl_->stop(); }
bool MeetingRegionSource::is_ready() const { return impl_->is_ready(); }

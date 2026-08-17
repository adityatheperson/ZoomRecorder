#include "meeting_region_source.h"

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

using namespace winrt;
namespace capture = winrt::Windows::Graphics::Capture;
namespace directx = winrt::Windows::Graphics::DirectX;
namespace direct3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {
direct3d::IDirect3DDevice make_device() {
  com_ptr<ID3D11Device> d3d;
  com_ptr<ID3D11DeviceContext> context;
  check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                  nullptr, 0, D3D11_SDK_VERSION, d3d.put(), nullptr, context.put()));
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
  MeetingRegionSourceImpl(HWND target, MeetingRegionSource::FrameCallback frame, MeetingRegionSource::HealthCallback health)
      : target_(target), frame_(std::move(frame)), health_(std::move(health)) {}

  bool start() {
    if (!IsWindow(target_)) { health_(false, "Zoom meeting window is unavailable"); return false; }
    try {
      init_apartment(apartment_type::multi_threaded);
      device_ = make_device();
      item_ = item_for_window(target_);
      pool_ = capture::Direct3D11CaptureFramePool::CreateFreeThreaded(device_, directx::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, item_.Size());
      frame_token_ = pool_.FrameArrived([this](auto const& sender, auto const&) {
        if (auto frame = sender.TryGetNextFrame()) {
          auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
          com_ptr<ID3D11Texture2D> texture;
          if (SUCCEEDED(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()))) {
            const auto now = std::chrono::steady_clock::now().time_since_epoch();
            frame_(texture.get(), std::chrono::duration_cast<std::chrono::nanoseconds>(now).count() / 100);
          }
          if (!ready_.exchange(true)) health_(true, "Meeting video ready");
        }
      });
      session_ = pool_.CreateCaptureSession(item_);
      session_.IsCursorCaptureEnabled(false);
      session_.StartCapture();
      return true;
    } catch (...) {
      health_(false, "Windows Graphics Capture could not start");
      stop();
      return false;
    }
  }

  void stop() {
    if (pool_) pool_.FrameArrived(frame_token_);
    if (session_) session_.Close();
    if (pool_) pool_.Close();
    session_ = nullptr; pool_ = nullptr; item_ = nullptr; device_ = nullptr; ready_ = false;
  }
  bool is_ready() const { return ready_; }

 private:
  HWND target_{};
  MeetingRegionSource::FrameCallback frame_;
  MeetingRegionSource::HealthCallback health_;
  std::atomic_bool ready_{};
  direct3d::IDirect3DDevice device_{nullptr};
  capture::GraphicsCaptureItem item_{nullptr};
  capture::Direct3D11CaptureFramePool pool_{nullptr};
  capture::GraphicsCaptureSession session_{nullptr};
  event_token frame_token_{};
};

MeetingRegionSource::MeetingRegionSource(HWND target, FrameCallback frame, HealthCallback health)
    : impl_(std::make_unique<MeetingRegionSourceImpl>(target, std::move(frame), std::move(health))) {}
MeetingRegionSource::~MeetingRegionSource() { impl_->stop(); }
bool MeetingRegionSource::start() { return impl_->start(); }
void MeetingRegionSource::stop() { impl_->stop(); }
bool MeetingRegionSource::is_ready() const { return impl_->is_ready(); }

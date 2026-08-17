#include "zoom_meeting_client.h"

#include <windows.h>
#include "auth_service_interface.h"
#include "meeting_service_interface.h"
#include "meeting_service_components/meeting_ui_ctrl_interface.h"
#include "zoom_sdk.h"

#include <cctype>
#include <string_view>

using namespace ZOOM_SDK_NAMESPACE;

namespace {
std::string json_string(std::string_view json, std::string_view name) {
  const auto marker = std::string{"\""} + std::string{name} + "\"";
  auto position = json.find(marker);
  if (position == std::string_view::npos) return {};
  position = json.find(':', position + marker.size());
  if (position == std::string_view::npos) return {};
  position = json.find('"', position + 1);
  if (position == std::string_view::npos) return {};
  std::string result;
  for (++position; position < json.size(); ++position) {
    const auto value = json[position];
    if (value == '"') break;
    if (value == '\\' && position + 1 < json.size()) {
      const auto escaped = json[++position];
      result.push_back(escaped == 'n' ? '\n' : escaped == 'r' ? '\r' : escaped == 't' ? '\t' : escaped);
    } else result.push_back(value);
  }
  return result;
}

std::wstring widen(const std::string& value) {
  if (value.empty()) return {};
  const auto size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
  if (size <= 0) return {};
  std::wstring result(size, L'\0');
  MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size);
  return result;
}
}

class ZoomMeetingClientImpl final : public IAuthServiceEvent, public IMeetingServiceEvent {
 public:
  explicit ZoomMeetingClientImpl(ZoomMeetingClient::EventSink sink) : sink_(std::move(sink)) {}
  ~ZoomMeetingClientImpl() override {
    if (meeting_) { meeting_->SetEvent(nullptr); DestroyMeetingService(meeting_); }
    if (auth_) { auth_->SetEvent(nullptr); DestroyAuthService(auth_); }
    if (initialized_) CleanUPSDK();
  }

  int prepare(const std::string& json) {
    meeting_id_ = widen(json_string(json, "MeetingId"));
    passcode_ = widen(json_string(json, "Passcode"));
    display_name_ = widen(json_string(json, "DisplayName"));
    jwt_ = widen(json_string(json, "Jwt"));
    if (meeting_id_.empty() || display_name_.empty() || jwt_.empty()) return 1;

    InitParam init;
    init.strWebDomain = L"https://zoom.us";
    init.enableLogByDefault = true;
    if (InitSDK(init) != SDKERR_SUCCESS) return 3;
    initialized_ = true;
    if (CreateAuthService(&auth_) != SDKERR_SUCCESS || !auth_) return 3;
    if (CreateMeetingService(&meeting_) != SDKERR_SUCCESS || !meeting_) return 3;
    auth_->SetEvent(this);
    meeting_->SetEvent(this);
    AuthContext context;
    context.jwt_token = jwt_.c_str();
    if (auth_->SDKAuth(context) != SDKERR_SUCCESS) return 3;
    sink_(R"({"type":"meeting_prepared"})");
    return 0;
  }

  int enter() {
    enter_requested_ = true;
    return authenticated_ ? join() : 0;
  }
  void set_host(HWND host) { host_ = host; }

  void onAuthenticationReturn(AuthResult result) override {
    if (result != AUTHRET_SUCCESS) { sink_(R"({"type":"failed","component":"zoom_authentication"})"); return; }
    authenticated_ = true;
    sink_(R"({"type":"zoom_authenticated"})");
    if (enter_requested_) join();
  }
  void onLoginReturnWithReason(LOGINSTATUS, IAccountInfo*, LoginFailReason) override {}
  void onLogout() override {}
  void onZoomIdentityExpired() override { sink_(R"({"type":"failed","component":"zoom_identity"})"); }
  void onZoomAuthIdentityExpired() override { sink_(R"({"type":"zoom_auth_expiring"})"); }
  void onNotificationServiceStatus(SDKNotificationServiceStatus, SDKNotificationServiceError) override {}

  void onMeetingStatusChanged(MeetingStatus status, int result) override {
    switch (status) {
      case MEETING_STATUS_CONNECTING: sink_(R"({"type":"meeting_connecting"})"); break;
      case MEETING_STATUS_INMEETING:
        attach_meeting_window(); sink_(R"({"type":"meeting_entered"})"); break;
      case MEETING_STATUS_ENDED:
        if (!ended_) { ended_ = true; sink_(R"({"type":"meeting_ended"})"); }
        break;
      case MEETING_STATUS_FAILED: sink_(R"({"type":"failed","component":"zoom_meeting"})"); break;
      default: break;
    }
  }
  void onMeetingStatisticsWarningNotification(StatisticsWarningType) override {}
  void onMeetingParameterNotification(const MeetingParameter*) override {}
  void onSuspendParticipantsActivities() override {}
  void onAICompanionActiveChangeNotice(bool) override {}
  void onMeetingTopicChanged(const zchar_t*) override {}
  void onMeetingFullToWatchLiveStream(const zchar_t*) override {}
  void onUserNetworkStatusChanged(MeetingComponentType, ConnectionQuality, unsigned int, bool) override {}
  void onAppSignalPanelUpdated(IMeetingAppSignalHandler*) override {}

 private:
  void attach_meeting_window() {
    if (!host_ || !meeting_) return;
    auto* ui = meeting_->GetUIController(); if (!ui) return;
    HWND first{}, second{}; if (ui->GetMeetingUIWnd(first, second) != SDKERR_SUCCESS || !first) return;
    SetParent(first, host_);
    SetWindowLongPtrW(first, GWL_STYLE, (GetWindowLongPtrW(first, GWL_STYLE) | WS_CHILD) & ~WS_POPUP);
    RECT bounds{}; GetClientRect(host_, &bounds); MoveWindow(first, 0, 0, bounds.right, bounds.bottom, TRUE);
    ShowWindow(first, SW_SHOW);
  }
  int join() {
    if (join_called_) return 2;
    unsigned long long meeting_number{};
    try { meeting_number = std::stoull(meeting_id_); } catch (...) { return 1; }
    JoinParam parameters;
    parameters.userType = SDK_UT_WITHOUT_LOGIN;
    auto& guest = parameters.param.withoutloginuserJoin;
    guest.meetingNumber = meeting_number;
    guest.userName = display_name_.c_str();
    guest.psw = passcode_.empty() ? nullptr : passcode_.c_str();
    guest.isVideoOff = false;
    guest.isAudioOff = false;
    join_called_ = true;
    return meeting_->Join(parameters) == SDKERR_SUCCESS ? 0 : 3;
  }

  ZoomMeetingClient::EventSink sink_;
  IAuthService* auth_{};
  IMeetingService* meeting_{};
  std::wstring meeting_id_, passcode_, display_name_, jwt_;
  bool initialized_{}, authenticated_{}, enter_requested_{}, join_called_{}, ended_{};
  HWND host_{};
};

ZoomMeetingClient::ZoomMeetingClient(EventSink sink) : impl_(std::make_unique<ZoomMeetingClientImpl>(std::move(sink))) {}
ZoomMeetingClient::~ZoomMeetingClient() = default;
int ZoomMeetingClient::prepare(const std::string& json) { return impl_->prepare(json); }
int ZoomMeetingClient::enter() { return impl_->enter(); }
void ZoomMeetingClient::set_host(HWND host) { impl_->set_host(host); }

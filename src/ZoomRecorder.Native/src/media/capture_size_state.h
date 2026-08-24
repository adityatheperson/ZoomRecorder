#pragma once

#include <windows.h>

class CaptureSizeState {
 public:
  bool observe(UINT width, UINT height);

 private:
  UINT width_{};
  UINT height_{};
};

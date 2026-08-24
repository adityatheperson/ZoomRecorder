#pragma once

#include <windows.h>

class CaptureSizeState {
 public:
  bool needs_recreate(UINT width, UINT height) const;
  void commit(UINT width, UINT height);

 private:
  UINT width_{};
  UINT height_{};
};

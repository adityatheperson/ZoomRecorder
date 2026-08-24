#include "aspect_fit.h"

bool run_aspect_fit_tests() {
  const auto exact = calculate_aspect_fit(1920, 1080, 1280, 720);
  if (exact.left != 0 || exact.top != 0 || exact.width != 1280 || exact.height != 720) return false;

  const auto pillar = calculate_aspect_fit(800, 600, 1280, 720);
  if (pillar.left != 160 || pillar.top != 0 || pillar.width != 960 || pillar.height != 720) return false;

  const auto letter = calculate_aspect_fit(800, 1200, 1280, 720);
  if (letter.left != 400 || letter.top != 0 || letter.width != 480 || letter.height != 720) return false;

  const auto even = calculate_aspect_fit(853, 479, 1279, 719);
  return even.left % 2 == 0 && even.top % 2 == 0 && even.width % 2 == 0 && even.height % 2 == 0;
}

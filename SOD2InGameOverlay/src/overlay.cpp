#include "overlay.h"
#include "game.h"
#include "log.h"

#include <imgui.h>
#include <cmath>

static bool  s_show    = true;
static bool  s_meters  = true;
static bool  s_velocity = true;

static const char* Compass(float x, float y) {
    static const char* names[] = {
        "N", "NNO", "NO", "ONO", "O", "OSO", "SO", "SSO",
        "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"
    };
    if (std::fabs(x) < 1.f && std::fabs(y) < 1.f) return "-";
    double deg = std::atan2(y, x) * 180.0 / 3.14159265358979323846;
    if (deg < 0) deg += 360.0;
    return names[static_cast<int>(std::lround(deg / 22.5)) % 16];
}

void OverlayInit() {
    // Nothing beyond ImGui context (created in hook.cpp) is needed.
}

void OverlayDraw() {
    if (!s_show) return;

    const PlayerState p = Game::Read();
    const float scale = s_meters ? 0.01f : 1.0f;
    const char* unit = s_meters ? "m" : "cm";

    ImGui::SetNextWindowPos(ImVec2(10, 10), ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(ImVec2(300, 0), ImGuiCond_FirstUseEver);
    ImGui::Begin("SOD2 - Koordinaten", &s_show,
                 ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_AlwaysAutoResize);

    if (!p.valid) {
        ImGui::TextColored(ImVec4(1.f, 0.45f, 0.4f, 1.f), "Kette ungueltig (Welt nicht geladen?)");
    } else {
        ImGui::Text("N  %10.2f %s", p.Location.X * scale, unit);
        ImGui::Text("O  %10.2f %s", p.Location.Y * scale, unit);
        ImGui::Text("H  %10.2f %s   (%s)", p.Location.Z * scale, unit,
                    Compass(p.Location.X, p.Location.Y));
        if (s_velocity && p.hasVelocity) {
            ImGui::Separator();
            ImGui::Text("vN %8.2f  vO %8.2f  vH %8.2f m/s",
                        p.Velocity.X * scale, p.Velocity.Y * scale, p.Velocity.Z * scale);
        }
    }

    ImGui::Separator();
    ImGui::Checkbox("Meter", &s_meters);
    ImGui::SameLine();
    ImGui::Checkbox("Velocity", &s_velocity);

    ImGui::End();
}

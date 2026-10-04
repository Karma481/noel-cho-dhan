# System-wide Ambient Light

Windows desktop app that samples the screen in real time (video, browsers, games), draws a
two-layer cinematic glow around the screen edges as a click-through overlay, and drives a physical
RGB LED strip (ESP32, Adalight protocol).

- **Stack:** C# / .NET 10 LTS, Vortice.Windows (DXGI Desktop Duplication, D3D11 compute, Direct2D,
  DirectComposition), Win32 comm API, WPF tray app; ESP32 firmware in C++ (PlatformIO, NeoPixelBus over RMT)
- **Targets:** < 16 ms present-to-LED latency, < 2% GPU overhead, zero allocations on the hot path
- **Laptop friendly:** the LED output is off by default and costs nothing until enabled; pausing, an
  exclusive-fullscreen game or turning the overlay off stops capture entirely; on hybrid laptops the
  integrated GPU that drives the panel is picked automatically

## Use

```powershell
dotnet publish src/AmbientLight.App -p:PublishProfile=win-x64
artifacts\publish\win-x64\AmbientLight.exe
```

One self-contained `AmbientLight.exe` (Windows 10 2004+ / 11, no .NET install needed). It lives in the
notification area:

| | |
|---|---|
| Tray menu | Settings…, Pause/Resume effect, Exit (double-click opens Settings) |
| Hotkeys | **Ctrl+Alt+L** pause/resume, **Ctrl+Alt+O** overlay on/off |
| Settings | *Virtual Overlay* (Subtle / Balanced / Cinematic presets, inner glow, ambient wash spreading up to half the screen), *Color & Blend* (Normal / Screen / Additive blend, keep letterboxed pictures clear, brightness, opacity, saturation up to 2×, contrast, luminance gain), *Performance & Mode* (30/60 FPS, hide in exclusive fullscreen, power-saving GPU, start with Windows), *Hardware LED* (on/off, COM port, baud rate) |
| Files | `%LOCALAPPDATA%\AmbientLight\config.json` (saved automatically), `logs\` |
| Command line | `--autostart` (start silently), `--exit` (close the running instance), `--verbose` (debug log) |

The overlay is excluded from screenshots, recordings and screen sharing (`WDA_EXCLUDEFROMCAPTURE`); where
Windows cannot guarantee that, it stays hidden rather than feed back into its own capture.

## Develop

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design,
[docs/TESTING-WINDOWS.md](docs/TESTING-WINDOWS.md) for the Windows test checklist, and
[firmware/esp32-adalight/README.md](firmware/esp32-adalight/README.md) for wiring and flashing the controller.

```bash
dotnet build -c Release
dotnet test -c Release

# Firmware (PlatformIO): unit tests on the PC, then build/flash
cd firmware/esp32-adalight
pio test -e native
pio run -e esp32dev -t upload

# Shader validation with the real FXC (cs_5_0) and DXC
tools/validate-shaders.sh

# Regenerate the app icons
python3 tools/generate-icons.py
```

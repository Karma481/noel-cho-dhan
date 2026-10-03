# System-wide Ambient Light

Windows desktop app that samples the screen in real time (video, browsers, games), draws a soft
glow around the screen edges as a click-through overlay, and drives a physical RGB LED strip
(ESP32, Adalight protocol).

- **Stack:** C# / .NET 10 LTS, Vortice.Windows (DXGI Desktop Duplication, D3D11 compute, Direct2D,
  DirectComposition), Win32 comm API; ESP32 firmware in C++ (PlatformIO, NeoPixelBus over RMT)
- **Targets:** < 16 ms present-to-LED latency, < 2% GPU overhead, zero allocations on the hot path

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design, and
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
```

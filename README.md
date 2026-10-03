# System-wide Ambient Light

Windows desktop app that samples the screen in real time (video, browsers, games), draws a soft
glow around the screen edges as a click-through overlay, and drives a physical RGB LED strip
(Arduino / ESP32, Adalight protocol).

- **Stack:** C# / .NET 10 LTS, Vortice.Windows (DXGI Desktop Duplication, D3D11 compute, DirectComposition)
- **Targets:** < 16 ms present-to-LED latency, < 2% GPU overhead, zero allocations on the hot path

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the stack decision, pipeline and threading
design, shared data types, project layout and dependencies.

```bash
dotnet build -c Release
dotnet test -c Release
```

# System-wide Ambient Light — Kiến trúc nền móng

> Trạng thái: **Phase 3 (Overlay) hoàn tất** — Foundation + Capture (mục 7) + Processing & letterbox (mục 8) + Overlay (mục 9).
> Serial/App được dựng ở các phase tiếp theo trên đúng các hợp đồng này.

---

## 1. Tech stack: **C# / .NET 10 (LTS) + Vortice.Windows 3.8.3**

### 1.1 Bảng đánh giá

| Tiêu chí | C# .NET 10 + Vortice | C++20 + D3D11/D2D | Ghi chú |
|---|---|---|---|
| Độ trễ pipeline | ≈ C++ | Tốt nhất (lý thuyết) | Hot path nằm trên **GPU** và **DWM**; phần CPU chỉ vài chục lệnh COM/frame (~µs). Chênh lệch ngôn ngữ < 0.1 ms. |
| GPU overhead | Như nhau | Như nhau | Do thiết kế shader/overlay quyết định, không do ngôn ngữ. |
| Rủi ro jitter | GC pause | Không | Loại bỏ bằng **zero-allocation steady state** (buffer cấp phát trước) + `GCSettings.LatencyMode = SustainedLowLatency`. |
| UI settings / tray | WPF + MVVM, rất nhanh | Win32/Qt/WinUI3 tốn công | ~60% code của app là UI, config, serial, logging. |
| Serial, JSON, DI, logging | Có sẵn trong BCL | Thư viện ngoài | `System.IO.Ports`, `System.Text.Json` source-gen. |
| An toàn bộ nhớ / bảo trì | Cao | Thấp hơn | COM lifetime qua `IDisposable`, không UB. |
| Interop DirectX | Vortice (SharpGen, mỏng, sát API gốc) | Native | Vortice 3.8.3 target `net10.0`, bao phủ DXGI 1.6, D3D11.4, DComp. |

### 1.2 Kết luận

**Chốt C# .NET 10 LTS + Vortice.Windows.** Lý do quyết định: ngân sách 16 ms bị chi phối bởi DWM (thời điểm frame được compose), GPU readback và đường truyền serial — không phải tốc độ ngôn ngữ. C++ chỉ thắng ở GC, và GC bị vô hiệu hoá trên hot path bằng thiết kế không cấp phát. Đổi lại, C# rút ngắn đáng kể phần UI/serial/config và dễ bảo trì hơn.

- .NET 10 thay cho .NET 8/9: .NET 10 là LTS hiện hành (hỗ trợ đến 11/2028); .NET 9 là STS hết hỗ trợ 11/2026; .NET 8 hết 11/2026.
- Điều kiện để lựa chọn này đúng (sẽ có benchmark + `dotnet-counters` kiểm chứng): **allocation rate = 0 B/s** trên các thread pipeline ở trạng thái ổn định.

---

## 2. Pipeline & Threading

### 2.1 Điều chỉnh quan trọng so với đề bài

Đề xuất ban đầu đặt *Downscale/Edge Extraction* trên Processing Thread (CPU). Làm vậy phải đọc ngược toàn bộ frame về RAM: 4K BGRA = **33 MB/frame ≈ 2 GB/s ở 60 fps** qua PCIe + memcpy — phá cả mục tiêu < 16 ms lẫn < 2% GPU.

➡️ **Downscale + trích xuất viền chạy bằng Compute Shader ngay trên GPU**, trong thread sở hữu D3D device (Capture). Chỉ **N zone × 16 byte** (≈ 1.6 KB cho 100 LED) được đọc về CPU. Processing Thread làm phần CPU thuần: hiệu chỉnh màu, làm mượt, giới hạn công suất.

### 2.2 Sơ đồ luồng dữ liệu

```
 DWM compose ─► [T1] Capture & GPU Reduce ───────────────────────────────────────────┐
                 AcquireNextFrame (DXGI Desktop Duplication, blocking, 0% CPU khi chờ) │
                 CS: lấy mẫu N×N/zone, sRGB→linear, HDR tone-map, average             │
                 ReleaseFrame ngay trước lần acquire kế tiếp (GPU đã xong)            │
                 ID3D11Fence + event → Map staging (~1.6 KB)                          │
                       │                                                              │
                       ▼  LatestValueMailbox<ZoneSampleFrame>   (triple buffer, wait-free)
                 [T2] Processing (CPU)
                   white balance → saturation → brightness → EMA smoothing (theo Δt)
                   → black threshold → LED gamma → power limiter (mA) → ColorRgb
                       │
                       ▼  LatestValueBroadcaster<FrameData>     (1 → N, mỗi consumer 1 triple buffer)
            ┌──────────┴───────────┐
            ▼                      ▼
   [T3] Overlay Render        [T4] Serial Output
   D3D11 device riêng         Adalight frame = header 6 B + RGB
   DirectComposition          ghi thẳng FrameData.ColorBytes (zero-copy)
   chỉ vẽ khi màu đổi         keep-alive 500 ms
```

### 2.3 Bảng thread

| Thread | Ưu tiên | Ngủ trên | Việc làm | Không bao giờ bị chặn bởi |
|---|---|---|---|---|
| **T1 Capture & GPU Reduce** | MMCSS "Capture" | `AcquireNextFrame` (DWM event) | Lấy frame, dispatch CS, readback | T2, T3, T4 |
| **T2 Processing** | AboveNormal | Mailbox event, hoặc timer 1 frame khi smoothing còn hội tụ | Pipeline màu CPU | T3, T4 |
| **T3 Overlay Render** | AboveNormal | Mailbox event | Upload ≤ 1 KB constant buffer, vẽ strip, `Present` | T4 |
| **T4 Serial Output** | AboveNormal | Mailbox event / keep-alive timeout | `SerialPort.Write` | Mọi thread khác |

Mỗi thread chỉ sở hữu tài nguyên của mình: T1 và T3 dùng **hai ID3D11Device riêng** → không chia sẻ immediate context, không cần `ID3D11Multithread`.

### 2.4 Cơ chế lock-free (đã hiện thực trong `AmbientLight.Core/Threading`)

| Primitive | Mô hình | Đảm bảo |
|---|---|---|
| `TripleBuffer<T>` | SPSC, 3 slot cấp phát trước | **Wait-free** cả hai phía: mỗi thao tác đúng 1 `Interlocked.Exchange`. Không torn read, luôn lấy frame mới nhất, frame cũ bị bỏ (latest-wins). |
| `LatestValueMailbox<T>` | Triple buffer + `AutoResetEvent` | Consumer ngủ trong kernel (0% CPU) khi không có dữ liệu. Event không mang dữ liệu và `Set()` không block producer. |
| `LatestValueBroadcaster<T>` | 1 producer → N mailbox | Mỗi consumer có bản sao riêng (vài trăm byte): serial bị nghẽn USB **không thể** làm overlay mất frame. |
| `SettingsHolder` | Immutable snapshot + volatile reference | Thread đọc `Current` một lần/vòng lặp → không bao giờ thấy cấu hình áp dụng dở dang. Lock chỉ ở phía ghi (UI), không nằm trên hot path. |

Bằng chứng: test `ConcurrentProducerAndConsumer_NeverObserveTornOrStaleFrames` chạy 2.000.000 frame song song, xác minh không có frame bị xé và sequence luôn tăng.

**Vì sao "latest-wins" là đúng:** với ánh sáng thời gian thực, hiển thị một màu cũ muộn còn tệ hơn bỏ qua nó. Mọi hàng đợi (queue) đều tích luỹ độ trễ khi consumer chậm; triple buffer thì không.

### 2.5 Ngân sách độ trễ (DWM present → LED sáng), 100 LED

| Chặng | Ước tính |
|---|---|
| DWM compose → `AcquireNextFrame` trả về | 0.5 – 1 ms |
| CS reduce + fence + readback 1.6 KB | 0.3 – 1 ms |
| Processing CPU | < 0.05 ms |
| Đánh thức thread (event) | < 0.1 ms |
| Truyền serial 306 B @ 2 Mbaud | 1.5 ms |
| USB latency (CH340/native CDC; **FTDI phải đặt latency timer = 1 ms**, mặc định 16 ms) | ≈ 1 ms |
| MCU xuất WS2812B (30 µs/LED) | 3 ms |
| **Tổng** | **≈ 7 – 9 ms** ✅ |

Đo thật bằng `FrameTiming`: DXGI `LastPresentTime` và `Stopwatch` cùng đồng hồ QPC → latency = phép trừ.

**Cảnh báo baud rate** (đã đưa vào `AppSettings.Validate()`): 100 LED @ 115200 baud mất **26.6 ms/frame** → không thể đạt 60 Hz. Cần ≥ 1.000.000 baud; 300 LED @ 1 Mbaud = 9.06 ms.

**Overlay ảo:** nội dung overlay chỉ hiện ở lần DWM compose kế tiếp, nên trễ ≥ 1 chu kỳ refresh (≈ 16.7 ms @ 60 Hz) so với nội dung gốc. Đây là giới hạn vật lý của mọi overlay trên Windows; glow là tín hiệu tần số thấp đã được làm mượt nên mắt không nhận ra.

### 2.6 Ngân sách GPU (< 2%)

- **Reduce:** 100 zone × 16×16 mẫu = 25.600 texel/frame — không đáng kể (< 0.05 ms).
- **Overlay:** render glow ở **1/8 độ phân giải**, để DirectComposition upscale bilinear. Glow là tần số thấp nên blur "miễn phí", chi phí fill giảm 64×. Chỉ `Present` khi màu thay đổi → màn hình tĩnh = 0 GPU.
- Chi phí còn lại là của DWM cho Desktop Duplication (cố định, mọi ứng dụng ambient đều chịu).

### 2.7 Rủi ro kỹ thuật đã nhận diện

| Rủi ro | Giải pháp |
|---|---|
| **Vòng phản hồi**: overlay nằm đúng vùng đang lấy mẫu → bị capture lại → màu tự khuếch đại | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (Win10 2004+), **đọc lại để xác minh**. Không loại trừ được thì overlay **không bao giờ hiện** (mục 9.2). |
| Video DRM (Netflix/Disney+ trong Edge/Chrome, PlayReady) | Desktop Duplication nhận vùng đen — giới hạn của OS. Ghi rõ trong UI; YouTube/trình duyệt thường và game không bị ảnh hưởng. |
| Game exclusive fullscreen | Windows 10/11 chuyển phần lớn sang flip-model/FSO nên duplication vẫn hoạt động; LED vẫn chạy. Overlay không thể vẽ đè exclusive fullscreen thật. |
| HDR | `IDXGIOutput5.DuplicateOutput1` với `R16G16B16A16_FLOAT` (scRGB), tone-map trong shader (`CaptureSettings.HdrToneMapping`). |
| Mất duplication (UAC, đổi độ phân giải, lock screen) | `DXGI_ERROR_ACCESS_LOST` → tái tạo duplication, `ZoneSampleFrame.LayoutVersion`/kích thước nguồn báo cho consumer. |
| Arduino AVR mất byte khi đang xuất LED (ngắt bị tắt) | Khuyến nghị **ESP32** (RMT/I2S DMA không chặn ngắt). Header Adalight có checksum để tự đồng bộ lại. |

---

## 3. Kiểu dữ liệu dùng chung (`AmbientLight.Core`)

| Kiểu | Loại | Vai trò | Quyết định thiết kế |
|---|---|---|---|
| `ColorRgb` | `readonly record struct`, **packed 3 byte** | Màu 8-bit cuối cùng | `MemoryMarshal.AsBytes(span)` = đúng payload Adalight, không copy. |
| `NormalizedRect` | `readonly record struct` (4 × float) | Vùng lấy mẫu 0..1 | Độc lập độ phân giải/DPI, map thẳng HLSL `float4`. |
| `ZoneConfig` | `readonly record struct`, blittable 24 B | 1 zone = 1 LED = 1 đoạn overlay | `Index` = vị trí trên dải LED → mảng zone đã theo thứ tự wire. Upload thẳng `StructuredBuffer`. |
| `ZoneLayoutBuilder` | static | Sinh zone từ số LED mỗi cạnh, góc bắt đầu, chiều chạy | Đã test đủ 4 góc × 2 chiều. |
| `ZoneSampleFrame` | class tái sử dụng | T1 → T2: màu **linear float** mỗi zone | Làm mượt/cân trắng trong không gian tuyến tính → đúng vật lý, không banding vùng tối. |
| `FrameData` | class tái sử dụng | T2 → T3/T4: `ColorRgb` cuối | `ColorBytes` zero-copy; `IsTransitioning` cho biết smoothing chưa hội tụ. |
| `FrameTiming` | `readonly record struct` | Timestamp QPC xuyên pipeline | Đo latency end-to-end bằng phép trừ. |
| `AppSettings` (+ `Capture/Processing/Overlay/Serial/LedLayoutSettings`) | `sealed record` bất biến | Cấu hình | JSON source-gen (AOT-safe), validate theo đường dẫn (`serial.baudRate`), ghi file nguyên tử (temp + rename). |
| `SettingsHolder` / `SettingsSnapshot` | class / record | Phát hành cấu hình cho thread | Settings + zone layout + version trong **một** snapshot nhất quán. |

---

## 4. Cấu trúc thư mục

```
noel-cho-dhan/
├── AmbientLight.slnx                     ✅ solution (định dạng XML mới của .NET 10)
├── global.json                           ✅ ghim SDK 10.0.1xx + Microsoft.Testing.Platform
├── Directory.Build.props                 ✅ nullable, analyzers latest-recommended, warnings-as-errors
├── Directory.Build.targets               ✅ quy ước riêng cho project test
├── Directory.Packages.props              ✅ Central Package Management — mọi phiên bản ở một chỗ
├── .editorconfig / .gitignore            ✅
├── docs/
│   └── ARCHITECTURE.md                   ✅ tài liệu này
├── src/
│   ├── AmbientLight.Core/                ✅ net10.0 — không phụ thuộc Windows, test được trên mọi OS
│   │   ├── Color/ColorRgb.cs
│   │   ├── Zones/ScreenEdge.cs, NormalizedRect.cs, ZoneConfig.cs, ZoneLayoutBuilder.cs
│   │   ├── Frames/ICopyFrom.cs, FrameTiming.cs, ZoneSampleFrame.cs, FrameData.cs
│   │   ├── Settings/AppSettings.cs, SettingsIssue.cs, AppSettingsJsonContext.cs,
│   │   │            AppSettingsStore.cs, SettingsHolder.cs
│   │   └── Threading/TripleBuffer.cs, LatestValueMailbox.cs, LatestValueBroadcaster.cs
│   ├── AmbientLight.Capture/             ✅ net10.0-windows — xem mục 7
│   │   ├── DesktopCaptureService.cs        thread T1, vòng lặp, phục hồi phân tầng
│   │   ├── CaptureStatus.cs, CaptureLog.cs
│   │   ├── Duplication/DesktopDuplicator.cs, AcquiredFrame.cs
│   │   ├── Gpu/CaptureDevice.cs, GpuZoneReducer.cs, ZoneReduceShader.cs, ZoneReduceConstants.cs
│   │   ├── ColorSpace/ColorSpaceMapping.cs, DisplayOutputInfo.cs, SurfaceOrientation.cs
│   │   ├── Recovery/CaptureRecovery.cs, RecoveryBackoff.cs
│   │   ├── Threading/PrecisionTimer.cs, MmcssRegistration.cs
│   │   ├── Interop/DisplayConfigInterop.cs, ThreadingInterop.cs
│   │   └── Shaders/ZoneReduce.hlsl
│   ├── AmbientLight.Processing/          ✅ net10.0 — xem mục 8
│   │   ├── ColorProcessingService.cs       thread T2
│   │   ├── Pipeline/ColorPipeline.cs, ColorPipelineParameters.cs, TemporalSmoother.cs, PowerLimiter.cs
│   │   ├── Letterbox/LetterboxDetector.cs
│   │   └── Color/ColorMath.cs, ColorTemperature.cs
│   ├── AmbientLight.Overlay/             ✅ net10.0-windows — xem mục 9
│   │   ├── OverlayService.cs, OverlayLog.cs    thread T3 + message loop
│   │   ├── Window/OverlayWindow.cs, OverlayWindowPolicy.cs, MonitorSelector.cs, MonitorEnumerator.cs
│   │   ├── Rendering/GlowRenderer.cs, GlowGeometry.cs, RedrawTracker.cs
│   │   └── Interop/User32.cs                   LibraryImport viết tay, kích thước struct chốt bằng test
│   ├── AmbientLight.Output.Serial/       Phase 4 — net10.0: Adalight encoder + writer, power limiter
│   └── AmbientLight.App/                 Phase 5 — net10.0-windows WPF: tray, settings UI, composition root
├── tests/
│   ├── AmbientLight.Core.Tests/          ✅ 38 test (xUnit v3 trên Microsoft.Testing.Platform)
│   ├── AmbientLight.Capture.Tests/       ✅ phần không cần GPU, chạy được cả trên Linux CI
│   ├── AmbientLight.Processing.Tests/    ✅ gồm test end-to-end đa luồng và test 0-allocation
│   ├── AmbientLight.Overlay.Tests/       ✅ style/affinity policy, chọn màn hình, hình học glow, redraw
│   └── AmbientLight.Output.Serial.Tests/ Phase 4
├── benchmarks/
│   └── AmbientLight.Benchmarks/          ✅ BenchmarkDotNet + MemoryDiagnoser (mục 8.5)
├── tools/
│   └── validate-shaders.sh               ✅ biên dịch mọi kernel bằng FXC thật (cs_5_0 /WX) + DXC
└── firmware/
    └── esp32-adalight/                   Phase 4 — PlatformIO + FastLED, RMT output
```

Quy tắc phụ thuộc (một chiều, không vòng): `App → {Capture, Processing, Overlay, Output.Serial} → Core`. Không project nào ngoài `App` được tham chiếu project anh em; chúng chỉ giao tiếp qua kiểu trong `Core`.

---

## 5. Dependencies (đã xác minh trên nuget.org, 10/2026)

| Package | Phiên bản | Dùng ở | Mục đích |
|---|---|---|---|
| Vortice.DXGI | 3.8.3 | Capture, Overlay | Desktop Duplication (`IDXGIOutput5.DuplicateOutput1`), swapchain |
| Vortice.Direct3D11 | 3.8.3 | Capture, Overlay | Compute shader, fence (`ID3D11Device5`), staging readback |
| Vortice.D3DCompiler | 3.8.3 | Capture, Overlay | Biên dịch HLSL (cache bytecode `.cso`) |
| Vortice.DirectComposition | 3.8.3 | Overlay | Visual tree trong suốt, upscale bilinear miễn phí |
| Vortice.Direct2D1 | 3.8.3 | Overlay | Device context trên swapchain, hiệu ứng Gaussian Blur |
| Vortice.Mathematics | 2.1.1 | Capture, Overlay | Kiểu vector/màu cho constant buffer |
| System.IO.Ports | 10.0.12 | Output.Serial | Cổng COM |
| Microsoft.Extensions.Hosting | 10.0.12 | App | DI, lifetime, cấu hình |
| Microsoft.Extensions.Logging.Abstractions | 10.0.12 | Các thư viện | `ILogger` không kéo theo host |
| Serilog.Extensions.Hosting | 10.0.0 | App | Logging có cấu trúc |
| Serilog.Sinks.File | 7.0.0 | App | Log file xoay vòng |
| CommunityToolkit.Mvvm | 8.4.2 | App | MVVM source-gen cho UI settings |
| H.NotifyIcon.Wpf | 2.4.1 | App | Icon khay hệ thống |
| xunit.v3 | 4.0.1 | tests | Test framework (chạy native trên Microsoft.Testing.Platform) |
| BenchmarkDotNet | 0.15.8 | benchmarks | Đo latency & allocation |

Yêu cầu môi trường: Windows 10 2004+ / Windows 11, GPU hỗ trợ D3D11.4 (feature level 11_0), .NET 10 SDK, Visual Studio 2026 hoặc Rider 2025.3+.

---

## 6. Lệnh phát triển

```bash
dotnet build -c Release      # 0 warning là bắt buộc (TreatWarningsAsErrors)
dotnet test  -c Release      # Microsoft.Testing.Platform
```

---

## 7. Phase 1 — `AmbientLight.Capture`

### 7.1 Vòng đời một frame (thread T1)

```
ReleaseFrame(frame trước)  ← nhả ngay trước acquire, theo khuyến nghị của Microsoft
AcquireNextFrame(timeout)  ── WAIT_TIMEOUT → màn hình tĩnh, không publish, consumer giữ màu cũ
   │ LastPresentTime == 0  → chỉ con trỏ chuột di chuyển → bỏ qua GPU hoàn toàn
   ▼
SRV trực tiếp trên surface (nếu có BIND_SHADER_RESOURCE) │ hoặc CopyResource GPU→GPU sang texture riêng
   ▼
Dispatch(ZoneCount) ZoneReduce.hlsl → CopyResource → staging
   ▼
ID3D11Fence.Signal + Flush → chờ kernel event (0% CPU)    │ fallback: Map chặn nếu driver không có fence
   ▼
Map staging (16 B/zone) → ZoneSampleFrame.WriteSlot → mailbox.Publish()  (wait-free)
   ▼
giữ frame cho tới vòng sau; nếu vượt MaxFps thì ngủ bằng waitable timer độ phân giải cao
```

### 7.2 Phục hồi lỗi (`DxgiErrorClassifier`)

| HRESULT | Nguyên nhân thực tế | Hành động |
|---|---|---|
| `DXGI_ERROR_WAIT_TIMEOUT` | Màn hình tĩnh | Không phải lỗi. Đếm `Timeouts`. |
| `DXGI_ERROR_ACCESS_LOST` | Bật/tắt HDR, đổi độ phân giải/xoay, chuyển fullscreen exclusive | Tạo lại **chỉ duplication**, lần đầu ngay lập tức. Đọc lại thông tin output (HDR, xoay) trước khi tạo. |
| `E_ACCESSDENIED`, `NOT_CURRENTLY_AVAILABLE`, `SESSION_DISCONNECTED`, `MODE_CHANGE_IN_PROGRESS` | UAC, màn hình khoá, Ctrl+Alt+Del, RDP ngắt, quá nhiều app duplicate | Trạng thái `WaitingForDesktop`, back-off 50 ms → 2 s, tự chạy lại khi khả dụng. |
| `DEVICE_REMOVED/RESET/HUNG`, `DRIVER_INTERNAL_ERROR`, `NOT_FOUND`, fence timeout 500 ms | Cập nhật/crash driver, TDR, rút màn hình | Huỷ toàn bộ, liệt kê lại output, tạo lại device + reducer + duplication. |
| Access lost lặp lại ≥ 5 lần liên tiếp | Output thật sự đã thay đổi | Leo thang lên tạo lại device. |
| `UNSUPPORTED`, format lạ, lỗi lập trình | Cấu hình không hỗ trợ | `Faulted`, thử lại mỗi 2 s — không bao giờ thoát vòng lặp, nên cắm lại màn hình hay cài driver vẫn tự hồi phục. |

Đổi settings capture hoặc layout LED → tạo lại duplication để frame đầu tiên chứa toàn bộ ảnh hiện tại, nên màn hình tĩnh vẫn nhận màu theo layout mới.

### 7.3 Ánh xạ không gian màu HDR/SDR

| Desktop | Format nhận về | Shader làm gì | Kết quả |
|---|---|---|---|
| SDR | `B8G8R8A8_UNorm` (sRGB-encoded) | Giải mã sRGB EOTF từng mẫu → tuyến tính | 0..1 tuyến tính |
| HDR | `R16G16B16A16_Float` (scRGB, 1.0 = 80 nit, primaries BT.709) | Chia cho `SdrWhite/80` → trắng SDR = 1.0; trung bình; gamut clip; roll-off | 0..1 tuyến tính |
| HDR, API cũ | `B8G8R8A8_UNorm` (DWM đã tone-map) | Như SDR | 0..1 tuyến tính |

- **Trung bình trong không gian tuyến tính** (sau decode, trước clip): vùng nửa đen nửa trắng ra 50% ánh sáng thật, không phải ~22% như khi trung bình giá trị gamma.
- **SDR white level** ("SDR content brightness" trong Windows) đọc qua CCD API `DISPLAYCONFIG_SDR_WHITE_LEVEL`, cập nhật mỗi 2 s vì kéo slider không gây access lost. Fallback 200 nit nếu Windows không trả về.
- **Gamut clip**: màu ngoài BT.709 (thành phần âm trong scRGB) được khử bão hoà về độ sáng của chính nó, giữ độ sáng và gần đúng sắc độ thay vì lệch màu như clamp từng kênh.
- **Roll-off highlight**: tuyến tính dưới knee 0.75; trên đó là extended Reinhard, **liên tục C1** tại knee và đạt đúng 1.0 tại độ sáng đỉnh EDID của màn hình (đã kiểm chứng số học). Hệ quả có chủ đích: trên desktop HDR, trắng SDR ra ≈ 0.88 để chừa chỗ phân biệt highlight; `Processing.Brightness` bù lại nếu cần. Tắt bằng `capture.hdrToneMapping = false` (khi đó cắt cứng ở trắng SDR).
- **Xoay màn hình**: Desktop Duplication luôn trả surface theo hướng gốc của panel. Các hình chữ nhật zone được xoay trên CPU (`SurfaceOrientation`) một lần mỗi khi layout/xoay đổi, nên shader không có logic xoay.

### 7.4 Quyết định đáng chú ý

| Quyết định | Lý do |
|---|---|
| Device tạo trên **adapter sở hữu output**, không phải GPU mạnh nhất | Laptop hybrid (iGPU lái màn hình) sẽ trả `DXGI_ERROR_UNSUPPORTED` nếu sai adapter. |
| `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` trên thread capture | `DuplicateOutput1` (bắt buộc cho FP16/HDR) yêu cầu DPI awareness này; đặt ở thread nên không phụ thuộc manifest của app. Không được thì fallback `DuplicateOutput` (SDR). |
| Đăng ký MMCSS task "Capture" + `ThreadPriority.AboveNormal` | Không bị game hay trình duyệt chiếm CPU làm đói thread. |
| `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` cho throttle MaxFps | `Thread.Sleep` làm tròn lên 15.6 ms. Mốc throttle tính từ lúc acquire trừ 1 ms slack, nên không thêm trễ khi tần số quét = MaxFps. |
| Biên dịch HLSL lúc chạy (`d3dcompiler_47.dll`, có sẵn trên Windows 10+) | HLSL là nguồn sự thật duy nhất; tốn vài chục ms một lần khi khởi động. |
| Bộ đệm GPU cấp phát một lần cho 1024 zone | Đổi layout không bao giờ cấp phát lại bộ nhớ GPU. |

### 7.5 Kiểm chứng và giới hạn

- **Đã kiểm chứng:** build 0 warning; 46 test cho phân loại HRESULT (đối chiếu với hằng số thật của Vortice), back-off, toán xoay (kể cả nghịch đảo), ánh xạ màu, kích thước struct Win32, và *hợp đồng HLSL ↔ C#* (thứ tự field cbuffer, stride, các giá trị `#define`). Shader biên dịch sạch bằng DXC `-WX` (cs_6_0).
- **Chưa kiểm chứng trên GPU thật:** môi trường phát triển là Linux, nên việc chạy trên Windows (FXC cs_5_0, duplication thực, fence, bind flags của surface) cần một lượt smoke test trên máy Windows có GPU.
- **Cấp phát bộ nhớ:** Vortice tạo 2 wrapper COM nhỏ mỗi frame (`IDXGIResource` từ `AcquireNextFrame` và `ID3D11Texture2D` từ `QueryInterface`). Dưới 10 KB/s ở 60 fps → GC gen0 vài phút một lần, mỗi lần < 100 µs. Mục tiêu "0 B" ở mục 1.2 đúng cho mọi buffer dữ liệu, không đúng tuyệt đối cho các wrapper interop này.

---

## 8. Phase 2 — `AmbientLight.Processing` và letterbox detection

### 8.1 Tương thích FXC `cs_5_0`

Tại runtime, Vortice gọi `d3dcompiler_47.dll`, tức FXC. Vì vậy shader được kiểm bằng **chính `fxc.exe` của Windows SDK 10.0.28000** (chạy qua Wine trên Linux) với `/T cs_5_0 /Ges /WX`, xem `tools/validate-shaders.sh`.

- Lượt kiểm đầu tiên **bắt được lỗi mà DXC bỏ sót**: cảnh báo X4000 "use of potentially uninitialized variable" ở các hàm có `return` sớm trong nhánh `if`. Tất cả helper đã viết lại theo kiểu single-exit. Hiện cả hai kernel biên dịch không một cảnh báo.
- Đã đối chiếu bảng binding do FXC sinh ra: `CSMain` dùng `cb0`, `t0` (stride 24), `t1`, `s0`, `u0` (stride 16); `CSEdgeProfile` dùng `cb0`, `cb1`, `t1`, `s0`, `u1`.
- Test hợp đồng còn chặn không cho đặt tên biến trùng keyword FXC (`line`, `point`, `triangle`, `sample`).

### 8.2 Letterbox / pillarbox

Màu của zone không đủ để phát hiện thanh đen: zone chỉ phủ 10% mép, còn thanh 21:9 trên 16:9 cao 12.8% mỗi bên. Vì vậy:

```
GPU  CSEdgeProfile: 256 hàng + 256 cột theo hướng nhìn thấy, mỗi đường 128 mẫu → độ sáng lớn nhất (2 KB)
 │   (cùng lần submit và fence với CSMain, không thêm điểm đồng bộ)
 ▼
T2  LetterboxDetector → ContentBounds ──► SnapshotCell<NormalizedRect> ──► T1 co zone vào nội dung
                                          (1 volatile read/frame; chỉ cấp phát khi thanh đen đổi)
```

| Quy tắc | Lý do |
|---|---|
| Mỗi trục đo bằng `min(trên, dưới)` | Phim luôn căn giữa. Bầu trời đêm chỉ làm *một* phía đen; phụ đề/logo chỉ làm *một* thanh sáng. |
| Mở rộng crop sau 1.5 s ổn định | Cảnh tối thoáng qua không bị cắt nhầm. |
| Thu hẹp crop **ngay lập tức** khi nội dung lấn vào *cả hai* thanh | Lấy mẫu bên trong nội dung luôn an toàn; lấy mẫu vào thanh đen làm LED tắt. |
| Frame đen hoàn toàn: bỏ qua | Không mang thông tin. |
| Thanh > 30%/phía: bỏ qua | Đó là cảnh tối, không phải thanh (2.76:1 trên 16:9 chỉ 17.7%). |
| Dung sai ±2 đường | Mép thanh rơi giữa hai đường mẫu sẽ dao động 1 đường. |

### 8.3 Pipeline màu

```
mẫu ─► black threshold ─► temporal smoothing ─► nhiệt độ màu ─► saturation ─► fit 0..1
                                                                              │
          ┌───────────────────────────────────────────────────────────────────┤
          ▼                                                                   ▼
  overlay: sRGB ─► DisplayColors          LED: white balance × brightness ─► sRGB ─► gamma LED ─► power limiter ─► LedColors
```

| Bước | Chi tiết |
|---|---|
| **Hai đầu ra** | `FrameData` có `LedColors` (wire-ready) và `DisplayColors` (sRGB cho màn hình). Cân trắng dải LED, độ sáng, gamma LED và giới hạn dòng **chỉ** áp cho LED; nếu áp cho overlay sẽ làm sai màu trên màn hình vốn đã được hiệu chỉnh. |
| Nhiệt độ màu | Locus Planck (spline Kim et al.) → XYZ → sRGB tuyến tính, chuẩn hoá để 6500 K = (1,1,1) và kênh lớn nhất = 1 (không bao giờ đẩy kênh nào vượt mức). 2700 K → (1, 0.44, 0.10). |
| Saturation | `Y + s·(c − Y)` quanh độ sáng của chính màu đó. Tự giảm `s` cho từng màu sát biên gamut để không kênh nào âm, nên giữ được sắc độ. Mặc định 1.2. |
| Temporal smoothing | EMA với `α = 1 − e^(−Δt/τ)`, **không phụ thuộc frame rate** (đã test 10×10 ms = 1×100 ms). Mỗi bước bị chặn tối đa 0.1 s, nên sau một lúc màn hình tĩnh lâu thì chuyển cảnh vẫn fade thay vì nhảy cóc. Service tiếp tục tick khi chưa hội tụ, và render lại khi settings đổi trên màn hình tĩnh. |
| Gamma LED | Mã hoá sRGB rồi `^LedGamma`, đúng như áp bảng gamma LED quen thuộc lên màu sRGB. Mặc định 2.2 gần như triệt tiêu, nên độ sáng LED tỉ lệ với ánh sáng thật của màn hình. |
| Power limiter | `I = n·I_idle + Σ(R+G+B)/255 · mA_kênh`. Vượt ngân sách thì nhân mọi kênh với cùng hệ số (giữ sắc độ), dùng **số học nguyên chính xác** nên được *đảm bảo* không vượt (test 2000 frame ngẫu nhiên). **Bật mặc định 400 mA**, an toàn cho cổng USB 2.0. Phản ứng tức thời, không làm mượt, vì đây là cơ chế an toàn. |

### 8.4 Zero-allocation

- Mọi buffer (target, trạng thái smoothing, `FrameData` scratch, profile) cấp phát **một lần** cho 1024 zone. Slot của triple buffer chính là memory pool, được tái sử dụng vô hạn. Toàn bộ dùng `Span<T>` và `Vector3`; tham số settings là struct, chỉ tính lại khi snapshot đổi (so sánh tham chiếu).
- Kiểm chứng: `ColorPipelineTests.SteadyState_AllocatesNothing` chạy 10.000 frame (letterbox đổi trạng thái liên tục) và assert `GC.GetAllocatedBytesForCurrentThread()` tăng **đúng 0 byte**.

### 8.5 Benchmark (BenchmarkDotNet ShortRun, Xeon 2.1 GHz, .NET 10)

| Phép đo | 100 LED | 1024 LED | Cấp phát |
|---|---:|---:|---:|
| `IngestAndRender` (toàn bộ pipeline) | 13.4 µs | 131 µs | 0 B |
| `RenderOnly` (tick smoothing) | 13.1 µs | 129 µs | 0 B |
| `LetterboxUpdate` (2 × 256 đường) | 0.04 µs | 0.04 µs | 0 B |
| `PowerLimit` | 1.1 µs | 0.9 µs | 0 B |

Ở 100 LED, 60 fps tốn khoảng 0.08% một nhân CPU. Chi phí chủ yếu là 6 phép `pow` mỗi zone (mã hoá sRGB cho hai đầu ra). Nếu sau này cần thì thay bằng LUT, nhưng hiện chưa cần.

---

## 9. Phase 3 — `AmbientLight.Overlay`

### 9.1 Cửa sổ Win32

| Yêu cầu | Hiện thực |
|---|---|
| Click xuyên qua | `WS_EX_LAYERED \| WS_EX_TRANSPARENT`, cộng thêm `WM_NCHITTEST → HTTRANSPARENT` để phòng hờ |
| Không bao giờ giành focus | `WS_EX_NOACTIVATE`; mọi `SetWindowPos` đều có `SWP_NOACTIVATE`; `WM_MOUSEACTIVATE → MA_NOACTIVATE`; hiện bằng `SWP_SHOWWINDOW`, không bao giờ `SetForegroundWindow` |
| Không xuất hiện ở taskbar / Alt+Tab | `WS_EX_TOOLWINDOW`, không có `WS_EX_APPWINDOW` |
| Luôn trên cùng | `WS_EX_TOPMOST` + `HWND_TOPMOST`; **đặt lại khi app khác lên foreground** (WinEvent hook `EVENT_SYSTEM_FOREGROUND`), không dùng timer nên không "giành giật" với các cửa sổ topmost khác |
| Không có bitmap GDI | `WS_EX_NOREDIRECTIONBITMAP`: nội dung duy nhất là visual DirectComposition, không tốn một bitmap toàn màn hình trong RAM |
| Toạ độ vật lý | Thread overlay đặt DPI awareness per-monitor-v2 trước khi tạo cửa sổ |

### 9.2 Loại trừ khỏi capture và fallback

```
Windows < 10.0.19041 ─────────────────► UnsupportedOperatingSystem ─┐
SetWindowDisplayAffinity(0x11) thất bại ► Failed ─────────────────────┼─► cửa sổ luôn ẩn, status CaptureExclusionUnavailable,
GetWindowDisplayAffinity ≠ 0x11 ───────► Failed ─────────────────────┘   LED vẫn chạy bình thường
còn lại ─────────────────────────────────► Excluded ─► được phép hiện
```

`Show()` tự từ chối nếu chưa loại trừ được, nên không có đường code nào hiện overlay khi capture có thể nhìn thấy nó. `WDA_MONITOR` **không** dùng làm fallback được: nó tô đen cửa sổ trong ảnh capture, mà cửa sổ này phủ toàn màn hình, nên mọi frame capture sẽ đen hết.

### 9.3 Render glow

```
OverlayWindowPolicy/GlowGeometry (CPU, 0 alloc)       GlowRenderer (D3D11 device riêng, trên adapter của màn hình đích)
  zone + DisplayColors ─► dải màu không chồng nhau ─► canvas D2D (kích thước render + margin, aliased)
                                                        ─► D2D Gaussian Blur (σ = blur radius / 3, border hard)
                                                        ─► back buffer swapchain composition (1/8 độ phân giải)
                                                        ─► Present(1) ─► Commit ─► DComp scale ×8, bilinear ─► DWM
```

| Tham số (`overlay.*`) | Mặc định | Ý nghĩa |
|---|---|---|
| `spreadFraction` | 0.04 | Spread width: độ dày dải màu đặc ở mỗi mép, tính theo phần cạnh ngắn màn hình (43 px ở 1080p, 86 px ở 4K) |
| `blurRadiusFraction` | 0.08 | Blur radius: khoảng glow mờ dần vào trong; σ = radius/3 |
| `opacity` | 0.85 | Độ đục tối đa |
| `brightness` | 1.0 | Cường độ màu |
| `resolutionDivisor` | 8 | Hệ số giảm độ phân giải khi render |

Dùng tỉ lệ thay vì pixel nên glow trông như nhau trên màn 1080p và 4K.

- **Không chồng lấn:** dải trên/dưới sở hữu các góc, dải trái/phải được cắt ngắn lại, nên không vùng nào bị blend alpha hai lần (góc sẽ không đậm hơn cạnh). Test kiểm từng cặp dải và kiểm cả vòng quanh màn hình: mỗi điểm được phủ đúng một lần.
- **Kéo ra margin:** dải chạm mép màn hình được kéo dài ra vùng margin ngoài màn hình, nên blur ở mép "thấy" thêm cùng màu thay vì vùng trong suốt. Mô phỏng Gaussian 1D trong test: độ sáng sát mép tăng từ ~45% lên ~92% (giới hạn lý thuyết Φ(spread/σ) với cấu hình mặc định).
- **Chỉ vẽ khi cần:** `RedrawTracker` so sánh byte (vector hoá) màu đã vẽ, layout, settings và danh sách zone. Không đổi thì không render, không `Present`. Đo bằng test: kiểm tra này 0 byte cấp phát.
- **Không nhấp nháy khi resize:** `Commit` của DirectComposition được hoãn đến sau `Present` đầu tiên, nên DWM không bao giờ compose một swapchain chưa có nội dung. Cửa sổ chỉ hiện sau lần present thành công đầu tiên.
- **Mất thiết bị** (`DEVICE_REMOVED/RESET/HUNG`, `D2DERR_RECREATE_TARGET`): chỉ dựng lại renderer, giữ nguyên cửa sổ.

### 9.4 Đa màn hình

- Màn hình được chọn **cùng quy tắc với Capture** (`capture.outputDeviceName`, null = primary), khớp theo tên GDI `\\.\DISPLAYn` mà cả DXGI lẫn `GetMonitorInfo` đều báo.
- Cửa sổ đặt đúng `rcMonitor` trong toạ độ virtual screen, kể cả màn hình phụ có toạ độ âm.
- `WM_DISPLAYCHANGE`, `WM_DPICHANGED`, `WM_SETTINGCHANGE` → liệt kê lại và đặt lại vị trí. Màn hình đích bị rút → ẩn overlay, thử lại mỗi 2 s, tự hiện lại khi cắm vào.
- Đổi sang màn hình trên GPU khác → renderer được dựng lại trên adapter mới.

### 9.5 Chi phí GPU và giới hạn

- **Khi màu không đổi:** không render, không present. Chỉ còn chi phí cố định của DWM khi compose một texture nhỏ (480×270 ở 4K), phóng to bằng bilinear.
- **Khi màu đổi:** một lần fill khoảng 100 hình chữ nhật và một lần blur trên canvas khoảng 526×316 px.
- **Game fullscreen:** một cửa sổ topmost phủ lên game toàn màn hình có thể khiến DWM không dùng được *independent flip*, trừ khi GPU hỗ trợ multiplane overlay. Exclusive fullscreen thật thì che overlay hoàn toàn. Nếu cần, App (Phase 5) có thể thêm tuỳ chọn tự ẩn overlay khi app fullscreen đang ở foreground.
- **Chưa kiểm chứng trên Windows thật:** sự kết hợp `WS_EX_LAYERED` + `WS_EX_NOREDIRECTIONBITMAP` + DirectComposition, việc `WDA_EXCLUDEFROMCAPTURE` loại cửa sổ khỏi Desktop Duplication, và hành vi topmost trên game borderless. Đây là các mục ưu tiên của lượt smoke test trên Windows.

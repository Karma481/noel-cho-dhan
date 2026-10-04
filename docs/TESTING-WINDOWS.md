# Build, đóng gói và test trên Windows

Hướng dẫn chạy thử app trên Windows thật: từ source, từ một file `AmbientLight.exe`, và checklist kiểm tra từng tính năng từ Phase 1 đến Phase 6. Mỗi bước ghi rõ **kết quả mong đợi** và **cách xác nhận**: nhìn bằng mắt, đọc log, hoặc mở `config.json`.

Đường dẫn dùng xuyên suốt:

| | |
|---|---|
| Cấu hình | `%LOCALAPPDATA%\AmbientLight\config.json` |
| Log | `%LOCALAPPDATA%\AmbientLight\logs\ambientlight-<yyyyMMdd>.log` |
| Exe sau khi publish | `artifacts\publish\win-x64\AmbientLight.exe` |

Chỉ sửa tay `config.json` khi app đã **Exit**. App không theo dõi file này, và khi đang chạy, lần tự lưu kế tiếp sẽ ghi đè thay đổi tay.

Mẹo: theo dõi log trực tiếp bằng PowerShell trong khi test:

```powershell
Get-Content "$env:LOCALAPPDATA\AmbientLight\logs\ambientlight-$(Get-Date -Format yyyyMMdd).log" -Wait -Tail 20
```

---

## 0. Yêu cầu

- Windows 10 version 2004 (build 19041) trở lên, hoặc Windows 11. Bản cũ hơn vẫn chạy nhưng overlay sẽ tự ẩn (mục 3.4).
- GPU hỗ trợ Direct3D 11 feature level 11_0.
- Chỉ để build: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Chạy file exe đã publish thì **không** cần cài .NET.

## 1. Build và chạy từ source

```powershell
git clone <repo> ; cd noel-cho-dhan
dotnet build -c Release            # phải ra 0 warning, 0 error
dotnet test  -c Release            # 423 test, tất cả pass
dotnet run --project src/AmbientLight.App -c Release
dotnet run --project src/AmbientLight.App -c Release -- --verbose   # log mức Debug
```

## 2. Đóng gói thành một file exe

```powershell
dotnet publish src/AmbientLight.App -p:PublishProfile=win-x64
```

Kết quả là **một file** `artifacts\publish\win-x64\AmbientLight.exe` (~157 MB): self-contained, ReadyToRun, không có DLL hay pdb đi kèm. Lệnh tương đương khi không dùng profile:

```powershell
dotnet publish src/AmbientLight.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false -o artifacts\publish\win-x64
```

- Thêm `-p:EnableCompressionInSingleFile=true` nếu cần file nhỏ hơn (~70 MB) để gửi đi. Đổi lại app tốn thêm RAM mỗi lần chạy (xem ARCHITECTURE mục 11.6).
- Lần chạy đầu, DLL native của WPF được giải nén một lần vào `%TEMP%\.net\AmbientLight\`. Từ lần sau sẽ khởi động nhanh hơn.
- Exe chưa ký số. Nếu nó được tải qua trình duyệt hoặc chat, SmartScreen có thể cảnh báo: chọn **More info → Run anyway**. Exe tự build trên máy thì không bị cảnh báo.

Tham số dòng lệnh:

| Lệnh | Tác dụng |
|---|---|
| `AmbientLight.exe` | Chạy và mở Settings. Nếu app đang chạy: chỉ mở Settings của instance đó. |
| `AmbientLight.exe --autostart` | Chạy im lặng trong tray (Run key dùng tham số này) |
| `AmbientLight.exe --exit` | Yêu cầu instance đang chạy thoát có kiểm soát (dùng trước khi thay exe bản mới) |
| `AmbientLight.exe --verbose` | Ghi log mức Debug |

---

## 3. Checklist Phase 5 (App) và Phase 6 (overlay cinematic)

### 3.1 Lần chạy đầu

1. Xoá `%LOCALAPPDATA%\AmbientLight` nếu đã có, rồi chạy `AmbientLight.exe`.
2. **Mong đợi:**
   - Cửa sổ Settings mở ra và icon xuất hiện trong khay hệ thống. Trên Windows 11 icon có thể nằm trong nhóm ẩn (^); kéo nó ra taskbar cho tiện.
   - Viền màn hình phát sáng theo màu nội dung.
   - `config.json` được tạo với `"serial": { "enabled": false, ... }`.
3. **Log** có các dòng:

   ```
   Ambient Light 0.1.0.0 starting on Microsoft Windows NT 10.0.2xxxx.0
   Created ...\config.json with the default settings
   Capturing \\.\DISPLAY1 (1920x1080, ...) on Intel(R) ...      ← tên GPU đang capture
   Overlay window is excluded from screen capture (WDA_EXCLUDEFROMCAPTURE)
   Pipeline "Running": capture=true overlay=true serial=false ...
   ```

   Không có dòng nào chứa `Serial output started`: khi LED tắt, thread serial không tồn tại.

### 3.2 Tray icon

| Thao tác | Mong đợi |
|---|---|
| Rê chuột lên icon | Tooltip `Ambient Light — Running` |
| Chuột phải | Menu **Settings…**, **Pause effect** (kèm Ctrl+Alt+L), **Exit** |
| **Pause effect** | Viền sáng biến mất, icon chuyển xám, mục menu đổi thành **Resume effect**. Task Manager › Details: CPU và GPU của AmbientLight về 0. |
| **Resume effect** | Viền sáng trở lại trong chưa tới 1 giây |
| Double-click icon | Mở (hoặc đưa lên trước) cửa sổ Settings |
| Đóng Settings bằng nút X | App **vẫn chạy** trong tray |
| **Exit** | Viền sáng tắt, icon biến mất, process kết thúc. Log có `Ambient Light exiting`, sau đó lần lượt `Capture stopped`, `Color processing stopped`, `Overlay stopped`. |

### 3.3 Hotkey toàn hệ thống

Test cả khi một app khác đang được focus (trình duyệt, Notepad):

| Phím | Mong đợi |
|---|---|
| **Ctrl+Alt+L** | Pause/Resume giống menu tray. Header Settings đổi `Running` ↔ `Paused`. |
| **Ctrl+Alt+O** | Bật/tắt overlay. Khi tắt overlay mà LED cũng tắt, log ghi `Pipeline "Idle"`: capture và processing cũng dừng hẳn. Công tắc "Show the glow overlay" trong Settings cập nhật theo, và `config.json` lưu `overlay.enabled`. |

Nếu một app khác đã chiếm tổ hợp phím, footer Settings sẽ ghi `Ctrl+Alt+L taken by another app` và log có cảnh báo `Hotkey ... is already used`.

### 3.4 Overlay và `WDA_EXCLUDEFROMCAPTURE`

**a. Hiển thị:** mở một video nhiều màu ở chế độ toàn màn hình của trình duyệt (F11 hoặc nút fullscreen của YouTube). Viền phải đổi màu theo từng cạnh. Click và kéo cửa sổ ở sát mép màn hình vẫn được bình thường, vì overlay không chặn chuột. Gõ phím trong Notepad trong lúc video chạy: focus không bao giờ bị overlay lấy mất.

**b. Footer:** tab bất kỳ, dòng 2 của footer phải là:

```
Overlay: on · DISPLAY1 · hidden from screen capture ✓
```

**c. Ảnh chụp màn hình không chứa glow:** đây là phép thử chính của `WDA_EXCLUDEFROMCAPTURE`.

1. Chọn preset **Cinematic** cho dễ thấy.
2. Nhấn **Win+Shift+S** rồi chụp toàn màn hình, dán vào Paint. Thử thêm **PrtScn**.
3. **Mong đợi:** trên màn hình thật thấy viền sáng rõ, nhưng ảnh chụp **không có viền sáng** (chỉ có nội dung bên dưới).

**d. OBS / Teams / Discord:** tạo nguồn *Display Capture* trong OBS (thử cả hai method "Windows 10 (1903 and up)" và "DXGI Desktop Duplication"), hoặc chia sẻ màn hình trong Teams/Discord. Preview không được có viền sáng.

**e. Không có vòng phản hồi:** chính app cũng capture bằng Desktop Duplication, nên nếu overlay bị capture thì viền sáng sẽ tự nuôi chính nó.

1. Mở một ảnh **đen hoàn toàn** ở chế độ toàn màn hình (Photos, F11).
2. **Mong đợi:** viền tối hẳn và giữ tối, không tự sáng dần lên.
3. Chuyển sang ảnh trắng: viền trắng. Quay lại ảnh đen: viền tắt dần trong khoảng 0.3 giây (smoothing 80 ms).

**f. Fallback:** trên Windows cũ hơn 2004, hoặc khi Windows từ chối loại trừ khỏi capture, overlay **giữ ẩn** để tránh vòng phản hồi. Footer ghi `Overlay: hidden (...)`, log có `Overlay cannot be excluded from screen capture (...)`. LED vẫn hoạt động bình thường.

### 3.5 Game exclusive fullscreen

1. Chạy một game ở chế độ **Fullscreen** (không phải Borderless hay Windowed fullscreen).
2. **Mong đợi** trong khoảng 2 giây:
   - Log ghi `Exclusive fullscreen true`, sau đó `Pipeline "SuspendedForFullscreen"`.
   - Tooltip tray đổi thành `Overlay paused for a fullscreen game`.
   - AmbientLight không còn dùng GPU.
3. Alt+Tab ra Desktop: sau khoảng 2 giây overlay chạy lại (`Exclusive fullscreen false`).
4. Game **Borderless**: overlay vẫn chạy. Đây là chủ ý, vì DWM vẫn vẽ được glow lên game borderless.
5. Tắt tuỳ chọn "Hide the overlay in exclusive fullscreen" (tab Performance & Mode): overlay không còn tự tạm dừng nữa.

### 3.6 Laptop hai GPU (iGPU + dGPU)

1. Trong log, dòng `Capturing \\.\DISPLAY1 ... on <GPU>` phải ghi **GPU tích hợp** (Intel Iris/UHD, AMD Radeon Graphics). Dòng `Overlay renders on <GPU>` cũng vậy.
2. Mở **Settings › System › Display › Graphics**: `AmbientLight.exe` có mặt với lựa chọn **Power saving** do app tự đăng ký. Nếu Han đã chọn sẵn một mục khác, app giữ nguyên lựa chọn đó.
3. **Thử đường dự phòng:** đổi sang **High performance**, Exit rồi chạy lại app. Capture vẫn phải hoạt động. Log có thể có:

   ```
   Capture interrupted ("SwitchAdapter"): Desktop Duplication is not supported from 'NVIDIA GeForce ...'. Retrying in 0 ms (attempt 1)
   Capturing \\.\DISPLAY1 (...) on Intel(R) ...
   ```

   Nghĩa là app tự chuyển sang iGPU mà không crash. Xong thì trả lại **Power saving**.
4. Nếu cắm màn hình ngoài: cổng HDMI/USB-C của nhiều laptop gaming nối thẳng vào dGPU. Chọn màn hình đó bằng `"capture": { "outputDeviceName": "\\\\.\\DISPLAY2" }` trong `config.json`, và capture sẽ chạy trên dGPU.

### 3.7 Start with Windows

1. Tab **Performance & Mode** → bật **Start with Windows**.
2. Mở Task Manager › **Startup apps**: có mục `AmbientLight` ở trạng thái **Enabled**.
3. Sign out rồi sign in lại: icon tray xuất hiện, **không** có cửa sổ nào mở ra, và viền sáng chạy.
4. Disable mục đó trong Task Manager rồi mở Settings: công tắc tắt, kèm ghi chú "Disabled in Task Manager › Startup apps…". Bật lại trong app thì Task Manager cũng chuyển về Enabled.
5. Di chuyển `AmbientLight.exe` sang thư mục khác rồi chạy: entry autostart tự trỏ về vị trí mới (log `Autostart entry re-pointed to ...`).

### 3.8 Settings tự lưu

| Thao tác | Mong đợi |
|---|---|
| Kéo một slider bất kỳ (Intensity, Width, Spread width, Blur radius, Brightness, Opacity, Saturation…) | Glow đổi ngay khi kéo. `config.json` được ghi khoảng 0.75 giây sau khi thả chuột (một lần cho cả cú kéo). |
| Chọn **30 FPS** | `capture.maxFps = 30`. GPU/CPU của app giảm rõ khi video đang chạy. |
| Sửa tay `config.json`: `"opacity": 5` rồi chạy lại app | Notification "Settings partly reset". Chỉ section `overlay` về mặc định, các section khác giữ nguyên. Có thêm file `config.invalid-<ngày-giờ>.json` chứa bản gốc. |
| Làm hỏng cú pháp JSON rồi chạy lại | Notification "Settings reset", có file backup, app chạy với giá trị mặc định |

### 3.9 Một instance

1. Khi app đang chạy và Settings đã đóng: double-click `AmbientLight.exe` → Settings của instance đang chạy mở ra và được đưa lên trước. Không có icon tray thứ hai.
2. `AmbientLight.exe --exit` trong PowerShell → app thoát có kiểm soát (log `Exit requested by another launch (--exit)`).

### 3.10 Hardware LED (khi có ESP32)

1. Nạp firmware theo [firmware/esp32-adalight/README.md](../firmware/esp32-adalight/README.md).
2. Tab **Hardware LED**: chọn COM port (bấm **Refresh** sau khi cắm board), chọn baud bằng `SERIAL_BAUD_RATE` của firmware (mặc định 1,000,000), rồi bật **Drive an LED strip**.
3. **Mong đợi:** footer ghi `LED strip: connected · COM3 · 60 fps`, dải LED theo màu màn hình.
4. Rút cáp: `COM3 not found, plug in the controller`. Cắm lại: tự kết nối lại trong khoảng 1 giây.
5. Bật LED mà chưa chọn cổng: footer báo lỗi màu cam `(not applied yet)` và chưa có gì được áp dụng.
6. Exit app: dải LED tắt ngay (frame đen). Nếu app bị kill: firmware tự fade về đen sau 2 giây.

### 3.11 Mức tiêu thụ tài nguyên

Ghi lại các số đo này để so sánh giữa các bản. Mở Task Manager › **Details** và thêm các cột *CPU*, *GPU*, *GPU engine*, *Memory (private working set)*.

| Trạng thái | Kỳ vọng |
|---|---|
| Desktop tĩnh, overlay bật | CPU ≈ 0 %: capture chỉ chạy khi màn hình thay đổi |
| Video 60 fps, overlay bật | CPU thấp, GPU vài phần trăm |
| Paused, Idle, hoặc game exclusive fullscreen khi LED tắt | CPU 0 %, GPU 0 % |
| Settings đóng so với mở | RAM giảm sau khi đóng cửa sổ một lúc |

### 3.12 Overlay cinematic (Phase 6)

Chuẩn bị: một video nhiều màu neon (tím, hồng, xanh) ở chế độ toàn màn hình, và một phim 21:9 có viền đen trên dưới. Chi tiết thiết kế ở ARCHITECTURE mục 12.

Nếu `config.json` có từ bản trước, look hiện là **Custom** (không nút preset nào sáng) vì spread và blur cũ được giữ. Bấm một preset để có look mới.

**a. Preset** (tab Virtual Overlay):

| Thao tác | Mong đợi |
|---|---|
| Bấm **Subtle** | Quầng sáng dịu sát mép, nội dung gần như không bị nhuộm |
| Bấm **Balanced** | Mép sáng rõ, ánh sáng loang vào khoảng 1/5 màn hình |
| Bấm **Cinematic** | Mép rực, màu loang sâu vào trong như tường phòng chiếu; tím/hồng/xanh neon đậm hơn hẳn |
| Kéo một slider bất kỳ sau khi chọn preset | Nút preset tắt sáng, mô tả đổi thành `Custom look. Pick a preset to start over from it.` |
| Mở `config.json` sau khi bấm Cinematic | `"blendMode": "Additive"`, `"spreadFraction": 0.35`, `"blurRadiusFraction": 0.45`, `"saturation": 1.8` |

**b. Hai lớp glow:** chọn Balanced, kéo **Ambient wash › Intensity** về 0 %: chỉ còn viền sáng hẹp sát mép (inner glow). Trả lại 40 %, rồi kéo **Inner glow › Intensity** về 0 %: chỉ còn lớp loang rộng, mép không còn "phát quang". Kéo **Spread width** và **Blur radius** lên 50 %: ánh sáng bốn cạnh gặp nhau ở giữa màn hình, góc không đậm hơn cạnh và không có đường chéo cứng.

**c. Blend mode** (tab Color & Blend), xem trên nền desktop tối rồi trên một trang web trắng:

| Mode | Mong đợi |
|---|---|
| Normal | Glow phủ lên nội dung như một lớp màu mờ, cả trên nền trắng |
| Screen | Nền tối được nhuộm màu rõ; trên nền trắng glow nhẹ hơn Normal |
| Additive | Trên nền tối rực nhất, ánh sáng "cộng" vào nội dung; trên nền trắng mép có thể cháy trắng. **Đây là mục chưa kiểm chứng được trong container:** nếu Additive không hiện gì hoặc hiện như Normal, ghi lại GPU và driver. |

**d. Color grading:** kéo **Saturation** từ 1.0× lên 2.0×: màu glow đậm dần, còn dải LED (nếu có) không đổi. Kéo **Contrast** lên 2.0× trên một cảnh tối: glow ở vùng tối gần như tắt, vùng sáng vẫn rực.

**e. Video letterbox:** mở phim 21:9 toàn màn hình, bật **Keep the picture clear** (mặc định bật).

1. Sau khoảng 1.5 giây (letterbox detection), glow chỉ nằm trong hai viền đen: sáng nhất sát mép hình, tối dần về phía bezel. Hình phim **không** bị nhuộm hay mờ, kể cả ở Cinematic.
2. Tắt Keep the picture clear: glow quay lại phát từ mép màn hình và loang vào hình.
3. Chuyển sang video 16:9 phủ kín màn hình: glow trở lại mép màn hình như bình thường.

**f. Chi phí:** Task Manager › Details khi video chạy ở Cinematic trên màn 4K: GPU của AmbientLight vẫn ở mức vài phần trăm. Ghi lại số đo cùng với mục 3.11.

---

## 4. Kiểm tra lại Phase 1–4 trên Windows

| Phase | Test | Mong đợi |
|---|---|---|
| 1 Capture | Win+L rồi đăng nhập lại; mở hộp thoại UAC | Footer `Capture: waiting for the desktop…`, sau đó tự chạy lại |
| 1 Capture | Đổi độ phân giải, xoay màn hình, bật/tắt **HDR** trong Settings › Display | Log `Capture interrupted ("RecreateDuplication")` và chạy lại trong vòng 1 giây. Khi HDR bật, footer có `· HDR` và màu glow không bị bệt. |
| 1 Capture | Phát video có DRM (Netflix trên Edge) | Footer có `protected video is black to capture`. Đây là giới hạn của Windows, không phải lỗi. |
| 2 Processing | Phim 21:9 có viền đen trên dưới | Sau khoảng 1.5 giây glow lấy màu từ phần hình, không lấy từ viền đen (letterbox detection) |
| 2 Processing | Exit app, đặt `"processing": { "colorTemperatureK": 4000 }` trong `config.json`, rồi chạy lại | Glow ấm hơn rõ rệt |
| 3 Overlay | Hai màn hình, `capture.outputDeviceName = "\\\\.\\DISPLAY2"` | Overlay chuyển sang màn hình 2 |
| 3 Overlay | Scale 125 %, 150 %, 175 % | Glow ôm sát mép màn hình, không lệch |
| 4 Serial | Theo mục 3.10 | |
| Shader | Chạy `tools/validate-shaders.sh` với FXC của Windows SDK | Mọi kernel biên dịch cs_5_0 với `/WX` |

## 5. Khi có vấn đề

| Triệu chứng | Nguyên nhân / cách xử lý |
|---|---|
| Không thấy icon tray | Windows 11 gom icon vào menu ^. Kéo ra taskbar, hoặc vào Settings › Personalization › Taskbar › Other system tray icons. Nếu Explorer khởi động chậm, app tự thử lại mỗi 5 giây. |
| Không có glow, footer `Overlay: hidden (...)` | Windows cũ hơn 2004, hoặc Windows từ chối `WDA_EXCLUDEFROMCAPTURE`. Overlay cố ý giữ ẩn. |
| Glow quá gắt, hình bị cháy trắng | Chọn blend mode **Screen** hoặc preset **Balanced**; với phim có viền đen, bật **Keep the picture clear** |
| Footer `Capture: error, retrying` | Xem dòng `Capture failed` trong log. Trên laptop hai GPU: chọn **Power saving** cho AmbientLight (mục 3.6). |
| Hotkey không ăn | Footer có `taken by another app`: một app khác đã đăng ký tổ hợp đó |
| Header báo lỗi màu đỏ | Một stage không khởi động được; lý do nằm ngay trên header và trong log (`could not start`) |
| Muốn log chi tiết | Exit, rồi chạy `AmbientLight.exe --verbose` |

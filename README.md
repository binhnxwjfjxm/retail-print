# Retail Print

Ứng dụng Windows nhỏ chạy nền để cấu hình và gửi lệnh in tới máy in nhiệt trong mạng nội bộ.

## MVP hiện tại

- Cửa sổ mini, không sidebar/dashboard lớn.
- System Tray: bấm icon để mở lại; đóng cửa sổ chỉ thu xuống tray.
- Thiết lập tên máy in, IP, cổng, khổ 80/58 mm.
- In thử qua TCP ESC/POS, mặc định cổng 9100.
- Lưu cấu hình tại `%AppData%\RetailPrint\settings.json`.
- Tùy chọn `Khởi động cùng Windows` bằng `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, không cần quyền Administrator; khi Windows khởi động app vào thẳng System Tray, không bật cửa sổ giữa màn hình.
- Chỉ chạy một instance; mở lại Retail Print sẽ gọi cửa sổ của instance đang chạy thay vì tạo tiến trình in thứ hai.
- Không tự retry lệnh in khi kết quả gửi không chắc chắn.

## Yêu cầu phát triển

- Windows 10/11.
- .NET 8 SDK.

## Chạy phát triển

```powershell
dotnet run --project .\RetailPrint\RetailPrint.csproj
```

## Build bản chạy độc lập

```powershell
.\build.ps1
```

File publish nằm trong `publish\win-x64`.

## Luồng sử dụng

1. Mở Retail Print.
2. Nhập IP máy in và cổng (thường là 9100).
3. Chọn 80 mm hoặc 58 mm.
4. Bấm `In thử`.
5. Nếu máy in chạy đúng, bấm `Lưu`.
6. Có thể bật `Khởi động cùng Windows`.
7. Đóng cửa sổ; ứng dụng tiếp tục chạy dưới System Tray.

## Bước tiếp theo

Sau khi shell Windows ổn định, bổ sung giao tiếp an toàn với Retail PWA. Phần PWA không gọi raw TCP trực tiếp tới máy in; Retail Print sẽ nhận lệnh từ kênh kết nối đã ghép nối rồi gửi tới IP máy in trong LAN.

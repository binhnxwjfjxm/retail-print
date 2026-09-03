# Retail Print

Ứng dụng Windows chạy nền để nhận lệnh in từ Retail và gửi tới máy in đã thiết lập trên máy tính.

## Cách dùng khuyến nghị

1. Cài `RetailPrint-Setup-win-x64.exe` theo tài khoản Windows hiện tại, không cần quyền Administrator.
2. Mở Retail Print.
3. Chọn `Máy in trên Windows` để dùng máy in đã có trong `Printers & scanners`.
4. Chọn máy in, khổ giấy 80/58 mm và bấm `In thử`.
5. Bấm `Lấy mã`, sau đó nhập mã 8 ký tự trong `Thiết lập máy in` trên Retail.
6. Khi Retail Print báo trực tuyến, Retail có thể gửi lệnh in.

Retail không cần biết máy in đang dùng USB, LAN, Wi-Fi hay máy in được chia sẻ. Nếu Windows đã cài và in được thì Retail Print có thể chọn máy đó.

## Máy in mạng trực tiếp

Giữ lại chế độ `Máy in mạng trực tiếp` cho máy in nhiệt có địa chỉ mạng riêng và hỗ trợ nhận lệnh trực tiếp.

- Nhập địa chỉ máy in và cổng, mặc định thường dùng cổng 9100.
- Chọn khổ giấy đúng với máy in.
- Bấm `In thử` trước khi lưu.

Cấu hình cũ từ phiên bản trước chỉ có IP/cổng sẽ tiếp tục dùng chế độ mạng sau khi cập nhật; ứng dụng không tự chuyển máy in đang vận hành sang Windows.

## In qua Windows

Chế độ Windows gửi nội dung qua hàng đợi in và driver của Windows. Cách này phù hợp cho máy in USB, máy in mạng đã cài driver, máy in chia sẻ và giúp giữ nội dung tiếng Việt tốt hơn so với đường RAW/ESC-POS hiện tại.

Khổ giấy 58/80 mm trong Retail Print cần khớp với khổ giấy đã cấu hình trong driver máy in Windows.

## Vận hành

- Cấu hình lưu riêng trên máy Windows.
- Có thể mở cùng Windows, không cần quyền Administrator.
- Chỉ chạy một phiên; mở lần hai sẽ gọi lại cửa sổ đang chạy.
- Đóng cửa sổ chỉ thu xuống khay hệ thống và ứng dụng vẫn nhận lệnh in.
- Kết nối Hệ thống Công Ty bằng HTTPS; không mở cổng nhận từ Internet.
- Có nhật ký cục bộ để tránh tự in lại khi kết quả lần trước chưa xác định.
- Nếu ứng dụng lỗi ngay khi mở, thông tin chẩn đoán được ghi tại `%LOCALAPPDATA%\RetailPrint\Logs\startup.log` thay vì im lặng thoát.

## Build

Yêu cầu Windows 10/11, .NET 8 SDK và Inno Setup 6.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Đầu ra:

```text
publish\win-x64\RetailPrint.exe
publish\installer\RetailPrint-Setup-win-x64.exe
```

CI chạy build, khởi động thử chính EXE đã publish rồi mới tạo bộ cài. Artifact CI giữ 30 ngày.

Bộ cài hiện chưa có chữ ký Authenticode trong repository. Trước khi phát hành rộng cần ký bằng chứng thư ký mã được quản lý an toàn; không lưu chứng thư hoặc khóa ký trong source.

Mặc định ứng dụng kết nối API Công Ty production. Khi phát triển hoặc kiểm thử có thể đặt biến môi trường `RETAIL_PRINT_API_URL` thành một API tương thích.

Không lưu token, mật khẩu database hoặc khóa nhà cung cấp trong repository.

# Retail Print

Ứng dụng Windows nhỏ chạy nền để nhận lệnh in từ Retail và gửi tới máy in nhiệt trong mạng nội bộ.

## Chức năng

- Giao diện mini, dùng Segoe UI/ClearType.
- Icon riêng cho ứng dụng và System Tray.
- Thiết lập tên máy in, IP, cổng, khổ 80/58 mm.
- In thử trực tiếp tới máy in TCP/ESC-POS, mặc định cổng 9100.
- Lưu cấu hình riêng trên máy Windows.
- Khởi động cùng Windows, không cần quyền Administrator.
- Chỉ chạy một phiên; mở lần hai sẽ gọi lại cửa sổ đang chạy.
- Lấy mã kết nối 8 ký tự để ghép với Retail.
- Kết nối ra Hệ thống Công Ty bằng HTTPS; không mở cổng trên Windows.
- Gửi trạng thái trực tuyến, nhận hàng đợi lệnh in và xác nhận kết quả.
- Có nhật ký cục bộ để không tự in lại khi kết quả lần trước chưa chắc chắn.

## Luồng kết nối

1. Windows và máy in ở cùng mạng nội bộ.
2. Mở Retail Print, nhập IP/cổng/khổ giấy và bấm `In thử`.
3. Bấm `Lấy mã`.
4. Trên Retail: `Thiết lập máy in` → `Retail Print trên Windows` → nhập mã 8 ký tự.
5. Khi Windows hiển thị `Retail đang trực tuyến`, Retail có thể gửi lệnh in.
6. Retail Print nhận lệnh từ Hệ thống Công Ty và gửi tới máy in bằng TCP/ESC-POS.

Retail trên điện thoại không cần biết IP của máy Windows và không gọi trực tiếp cổng `9100`.

## Build

Yêu cầu Windows 10/11 và .NET 8 SDK trở lên.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

File đầu ra: `publish\win-x64\RetailPrint.exe`.

Mặc định ứng dụng kết nối API Công Ty production. Khi phát triển hoặc kiểm thử có thể đặt biến môi trường `RETAIL_PRINT_API_URL` thành một API tương thích.

Không lưu token, mật khẩu database hoặc khóa nhà cung cấp trong repository.

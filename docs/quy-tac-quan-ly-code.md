# Lưu ý mỗi khi thay đổi code

1. **Trước khi sửa:** xác định đúng file và hàm bị ảnh hưởng, kiểm tra project đang build được (`dotnet build`).
2. **Đặt code đúng folder theo trách nhiệm:** `Watching/`, `Reading/`, `Scanning/`... Không để file rời ở gốc. Core không được biết module cụ thể.
3. **Thống nhất namespace theo folder** (ví dụ `DlpService.Scanning`). Khi di chuyển file phải cập nhật `using` ở nơi dùng.
4. **Comment các đoạn AI sinh hoặc sửa** bằng tiếng Việt, đánh dấu `[Thay đổi]` cho chỗ sửa.
5. **Không log dữ liệu nhạy cảm nguyên văn** (CCCD, số thẻ), luôn che giá trị.
6. **Sau mỗi buổi:** cập nhật file tóm tắt `docs/buoi-XX-tom-tat.md`.

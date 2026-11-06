# Buổi 16: Viết lại tài liệu kiến trúc service

| Hạng mục | Trạng thái |
|---|---|
| Đọc lại code (`Program.cs`, `Worker.cs`, `ClassificationEngine.cs`, `appsettings.json`) và các tài liệu có sẵn | ✅ Xong |
| Tạo `docs/kien-truc-service.md`: tổng thể, tầng/trách nhiệm, vòng đời, Pass 1/2, dữ liệu, đồng thời, chịu lỗi, bảo mật, cấu hình, khoảng trống | ✅ Xong |
| Thêm dòng trỏ sang file mới ở đầu `kien-truc-trien-khai.md` và `tai-lieu-ky-thuat.md` | ✅ Xong |
| Sửa code `.cs` | Không |

## Ghi chú
- Ngưỡng `Score` lệch: `appsettings.json` là 0,80/0,10 nhưng mặc định trong code (`RouterOptions`) là 0,90/0,30 (đã ghi ở mục 9 của tài liệu mới).
- Gợi ý bước sau: tách `Worker.cs` thành các hosted service riêng (cần Change Brief riêng).

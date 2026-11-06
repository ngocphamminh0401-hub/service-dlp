> **Lưu ý (05/11/2026):** kiến trúc đúng với code hiện tại nằm ở `kien-truc-service.md`. File này giữ để tra lý do thiết kế/lịch sử.

# Kiến trúc triển khai — DLP Service (Regex + LLM thẩm định)

## 1. Mục tiêu, nguyên tắc, phạm vi

### 1.1 Mục tiêu

- Giảm false positive của lớp regex trong vùng điểm tin cậy thấp.
- Bổ sung khả năng bắt false negative theo ngữ cảnh (tên cột, tiêu đề, từ khóa) ở mức có kiểm soát.
- Không làm chậm luồng quét chính, không làm nặng máy người dùng.
- Dữ liệu thật không rời khỏi máy và không đi vào LLM (chỉ ngữ cảnh đã che).

### 1.2 Nguyên tắc thiết kế (bất biến)

| # | Nguyên tắc | Hệ quả |
|---|---|---|
| P1 | Regex là lớp chính, LLM là lớp thẩm định | LLM chỉ xử lý một phần nhỏ (mục tiêu 2 đến 10 phần trăm) |
| P2 | Quyết định cuối cùng nằm ở code, không nằm ở LLM | LLM chỉ điều chỉnh điểm; có luật an toàn khi merge |
| P3 | LLM lỗi thì hệ thống thoái hóa về regex-only, không dừng | Kết quả regex luôn được ghi trước (provisional) |
| P4 | LLM process stateless và không có quyền truy cập dữ liệu | Không đọc file, không biết DB, không gọi ngược |
| P5 | Mask trước khi gửi, ở phía có quyền đọc file | LLM chỉ thấy ngữ cảnh đã che |
| P6 | Mọi thứ đo được: shadow mode trước khi bật thật | Không đổi kết quả người dùng khi chưa có số liệu |

### 1.3 Ngoài phạm vi (giai đoạn này)

- OCR cho file scan (đánh dấu `needs_ocr`, không đẩy xuống LLM).
- Fine-tune model (chỉ chuẩn bị dữ liệu, xem giai đoạn cuối).
- Quét nội dung mạng, email server, database từ xa.
- Hành động chặn hoặc mã hóa tự động dựa trên kết quả LLM (chỉ gắn nhãn và cờ `needs_review`).

---

## 2. Kiến trúc tổng thể

### 2.1 Sơ đồ

```
┌───────────────────────── Máy người dùng (offline) ─────────────────────────┐
│                                                                            │
│  ┌────────────────────────────────────┐   Named Pipe (ACL)  ┌────────────┐ │
│  │ REGEX SERVICE  (chủ hàng đợi)      │◄───────────────────►│ LLM PROCESS│ │
│  │ Scanner → Extractor → Regex →      │  Classify / GetInfo │ stateless  │ │
│  │ Validator → Score(S1) → Router →   │  / Cancel           │ Phi-4-mini │ │
│  │ Queue → Chunk+Mask → Merge         │                     │ INT4, ONNX │ │
│  └───────────────┬────────────────────┘                     └────────────┘ │
│                  │                                                         │
│           ┌──────▼──────┐                                                  │
│           │   SQLite    │ scan_result, llm_job, llm_result, cache          │
│           └─────────────┘                                                  │
└────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Phân chia trách nhiệm

| Việc | Regex service | LLM process |
|---|---|---|
| Đọc file, trích text | Có | Không |
| Regex, validator, tính điểm S1 | Có | Không |
| Quyết định file nào xuống Pass 2 | Có | Không |
| Chọn chunk, mask | Có | Không |
| Hàng đợi, retry, timeout, ưu tiên, cache | Có | Không |
| Merge S1 + S2 ra kết quả cuối | Có | Không |
| Load model, dựng prompt, suy luận | Không | Có |
| Ép output theo schema | Không | Có |
| Báo tình trạng model, RAM | Không | Có |

### 2.3 Luồng xử lý một file (tuần tự theo thời gian)

1. Scanner phát hiện file (quét theo lịch, hoặc sự kiện thay đổi file).
2. Kiểm tra cache (path, mtime, size) rồi `file_hash`; nếu không đổi thì dùng lại kết quả.
3. Extractor trích text (streaming, có giới hạn) và giữ cấu trúc (tiêu đề, cột).
4. Regex engine tìm candidate, validator kiểm tra, tính điểm từng candidate và điểm file S1.
5. Ghi `scan_result` với trạng thái provisional (hoặc final nếu không cần Pass 2).
6. Router phân vùng: chắc chắn (>= T_high) thì final; vùng xám hoặc đáng nghi thì qua bộ lọc eligibility.
7. File đủ điều kiện được ghi `llm_job` (pending) kèm mức ưu tiên.
8. Worker (khi máy rảnh) lấy job, kiểm tra `file_hash` chưa đổi, trích lại và chọn chunk, mask.
9. Gửi Classify qua pipe, chờ kết quả với deadline.
10. Kiểm tra lại `file_hash`; nếu file đã đổi thì hủy kết quả và quét lại từ đầu.
11. Merge S1 và S2 theo chính sách, ghi `llm_result`, cập nhật `scan_result` thành final.

---

## 3. Mô hình dữ liệu và máy trạng thái

### 3.1 Lược đồ SQLite (đề xuất)

Bật `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;`

```sql
CREATE TABLE file_state (
  file_id      INTEGER PRIMARY KEY,
  path         TEXT NOT NULL UNIQUE,
  size         INTEGER NOT NULL,
  mtime_utc    INTEGER NOT NULL,
  file_hash    TEXT,               -- SHA-256 hoặc hash nhanh (xxhash) + size
  last_scan_utc INTEGER
);

CREATE TABLE scan_result (
  file_id      INTEGER PRIMARY KEY REFERENCES file_state(file_id),
  file_hash    TEXT NOT NULL,
  s1           REAL NOT NULL,      -- điểm regex cấp file
  s_final      REAL NOT NULL,
  status       TEXT NOT NULL,      -- provisional | final
  source       TEXT NOT NULL,      -- regex | llm | merged
  labels_json  TEXT,               -- nhãn cuối
  needs_review INTEGER NOT NULL DEFAULT 0,
  updated_utc  INTEGER NOT NULL
);

CREATE TABLE finding (
  id           INTEGER PRIMARY KEY,
  file_id      INTEGER NOT NULL REFERENCES file_state(file_id),
  file_hash    TEXT NOT NULL,
  rule_id      TEXT NOT NULL,
  score        REAL NOT NULL,
  validator_passed INTEGER,
  hard_rule    INTEGER NOT NULL DEFAULT 0,
  count        INTEGER NOT NULL,
  location     TEXT               -- sheet/cột/trang/offset đại diện, không chứa giá trị thật
);

CREATE TABLE llm_job (
  job_id       TEXT PRIMARY KEY,   -- GUID, dùng làm idempotency key
  file_id      INTEGER NOT NULL REFERENCES file_state(file_id),
  file_hash    TEXT NOT NULL,
  priority     INTEGER NOT NULL,   -- 1=P1 vùng xám, 2=P2 đáng nghi, 3=P3 lấy mẫu
  status       TEXT NOT NULL,      -- pending | running | done | failed | timeout | stale
  attempts     INTEGER NOT NULL DEFAULT 0,
  next_try_utc INTEGER NOT NULL DEFAULT 0,
  created_utc  INTEGER NOT NULL,
  updated_utc  INTEGER NOT NULL
);
CREATE INDEX ix_job_pick ON llm_job(status, priority, next_try_utc);

CREATE TABLE llm_result (
  job_id       TEXT PRIMARY KEY REFERENCES llm_job(job_id),
  json_out     TEXT NOT NULL,      -- JSON đã validate, không chứa dữ liệu thật
  s2           REAL,
  confidence   REAL,
  model_ver    TEXT NOT NULL,
  prompt_ver   TEXT NOT NULL,
  latency_ms   INTEGER,
  created_utc  INTEGER NOT NULL
);

CREATE TABLE llm_cache (
  pack_hash    TEXT NOT NULL,      -- hash của gói đã mask
  prompt_ver   TEXT NOT NULL,
  model_ver    TEXT NOT NULL,
  json_out     TEXT NOT NULL,
  created_utc  INTEGER NOT NULL,
  PRIMARY KEY (pack_hash, prompt_ver, model_ver)
);

CREATE TABLE audit (
  id           INTEGER PRIMARY KEY,
  ts_utc       INTEGER NOT NULL,
  event        TEXT NOT NULL,
  job_id       TEXT,
  detail       TEXT               -- chỉ hash, mã, nhãn; không nội dung
);
```

Nguyên tắc: không lưu snippet text trong bảng nào (mặc định). Xem mục 4.9 nếu chọn phương án "trích một lần".

### 3.2 Máy trạng thái của job

```
pending ──lấy job──► running ──OK──► done
   ▲                    │ ├─ lỗi tạm ─► pending (attempts+1, backoff)
   │                    │ ├─ quá deadline ─► timeout ─► pending (nếu attempts < max) | failed
   │                    │ └─ file đã đổi ─► stale (bỏ, quét lại từ Pass 1)
   └── khởi động lại service: running ──► pending
```

- `attempts` tối đa 3, backoff lũy thừa (1 phút, 5 phút, 20 phút).
- Job stale, failed không xóa ngay (giữ cho audit), dọn theo lịch (ví dụ 30 ngày).
- Ghi kết quả theo kiểu upsert theo `job_id`: xử lý trùng không gây hại (at-least-once + idempotent).

### 3.3 Máy trạng thái của kết quả file

(chưa quét) → provisional (sau Pass 1) → final (sau Pass 2 hoặc khi không cần Pass 2 hoặc khi LLM thất bại và chấp nhận điểm regex).

---

## 4. Regex service: phân tích chi tiết từng thành phần

### 4.1 Scanner (phát hiện file)

- Nguồn: quét theo lịch (full scan) và theo sự kiện (FileSystemWatcher hoặc USN Journal cho khối lượng lớn).
- Danh sách loại trừ: `C:\Windows`, `Program Files`, `node_modules`, thư mục cache, file hệ thống, file quá lớn (cấu hình).
- Trạng thái: bỏ qua nếu (path, mtime, size) không đổi so với `file_state`.
- Song song: nhiều file cùng lúc (giới hạn theo số lõi, mặc định số lõi logic trừ 2), mỗi file xử lý tuần tự trong luồng của nó.
- Ưu tiên thấp (`ThreadPriority.BelowNormal`, IO priority thấp) để không ảnh hưởng người dùng.

### 4.2 Extractor (trích text)

Yêu cầu chung: streaming, có giới hạn cứng (dung lượng đọc tối đa, số trang/dòng, thời gian), không thực thi macro, chạy trong try/catch với timeout; file lạ hoặc nghi ngờ thì chạy trong process con.

| Loại | Cách trích | Cấu trúc cần giữ |
|---|---|---|
| txt, log, csv, json, xml | Đọc trực tiếp, phát hiện encoding (UTF-8, UTF-16, Windows-1258) | Dòng, header csv |
| docx | OpenXML SDK: w:p, w:tbl, header/footer | Tiêu đề (style Heading), bảng thành cột \| cột |
| xlsx | OpenXML reader theo hàng (streaming) | Tên sheet, dòng đầu làm header, giá trị theo cột |
| pdf | Trích text layer | Số trang; bản scan (không có text layer) thì needs_ocr |
| eml, msg | Tách header và body | Subject, tên tệp đính kèm |
| pptx | Text theo slide | Tiêu đề slide |

- Bảo vệ: chống zip bomb (giới hạn tỉ lệ nén, số entry, dung lượng sau giải nén), giới hạn độ sâu file lồng nhau, timeout trích mỗi file (ví dụ 30 giây).
- Đầu ra của Extractor: chuỗi khối (block) có thuộc tính `{kind, text, location}` (kind: title, header, cell, paragraph, line), để các bước sau vừa quét regex vừa giữ được cấu trúc.

### 4.3 Normalizer

- Unicode NFC, gom khoảng trắng, bỏ ký tự điều khiển.
- Giữ hai dạng để khớp: dạng gốc có dấu và dạng bỏ dấu (cho từ khóa ngữ cảnh như "căn cước" và "can cuoc").
- Loại bỏ chuỗi trông như chỉ thị (ví dụ "ignore previous instructions") chỉ ở đường gửi LLM, không ở đường regex.

### 4.4 Rule engine (Tier 1)

Mỗi rule là cấu hình, không hard-code:

```yaml
- id: vn_cccd
  category: PII.NationalID
  pattern: '(?<!\d)\d{12}(?!\d)'
  validator: cccd_province_code      # 3 số đầu là mã tỉnh; số thứ 4 là giới tính/thế kỷ
  base_score: 0.50
  hard_rule: false                   # true nếu validator cứng (Luhn, checksum)
  context_boost:   ["cccd", "căn cước", "cmnd", "số định danh", "so dinh danh"]
  context_penalty: ["mã đơn", "order", "tracking", "invoice", "mã vận đơn"]
  llm_policy: verify_if_ambiguous    # always | verify_if_ambiguous | never
```

Yêu cầu kỹ thuật:

- Dùng `RegexOptions.NonBacktracking` (hoặc timeout mỗi match) để tránh ReDoS.
- Rule có tiền tố cố định (thẻ, API key) đưa vào bộ tìm nhiều mẫu (Aho-Corasick) để quét nhanh trước.
- Rule có version, nạp từ file cấu hình có chữ ký hoặc hash; đổi rule là đổi `rules_ver` và có thể kéo theo quét lại.
- Bộ rule khởi đầu gợi ý: CCCD/CMND, MST (10 hoặc 13 số), số điện thoại VN, email, số thẻ (Luhn), số tài khoản ngân hàng (theo ngữ cảnh), IBAN, secret/token (entropy), địa chỉ IP nội bộ (tùy chính sách), từ khóa lương/hợp đồng.

### 4.5 Validator

Lớp giá trị lớn nhất trước khi gọi LLM. Mỗi validator trả về passed và mức tin cậy:

| Rule | Validator |
|---|---|
| CCCD | Mã tỉnh hợp lệ, số thứ 4 hợp lệ, năm sinh hợp lý |
| MST | Checksum 10 số; 13 số = 10 số + 3 số chi nhánh |
| Thẻ | Luhn + BIN hợp lệ |
| IBAN | mod-97 |
| Token/secret | Entropy, độ dài, ký tự, tiền tố (AKIA, ghp_...) |
| Email, SĐT | Cú pháp + đầu số hợp lệ |

Validator cứng (Luhn đúng, checksum MST đúng) đặt `hard_rule=true`: LLM không được bác bỏ (xem mục 7).

### 4.6 Tính điểm

Cấp candidate:

```
p = clamp( base_score + boost(context) − penalty(context) + validator_adj , 0, 1 )
```

- boost: mỗi từ khóa ngữ cảnh gần match cộng một lượng nhỏ (tối đa giới hạn).
- penalty: từ khóa loại trừ trừ điểm.
- validator_adj: cộng nếu validator pass, trừ mạnh nếu fail.

Cấp file (S1): noisy-OR trên các candidate và điều chỉnh theo mật độ:

```
S1 = 1 − Π(1 − p_i)        (trên các candidate đã gộp theo rule/cột)
S1 = min(1, S1 + density_bonus)   # mật độ cao (nhiều match trong một cột) tăng độ tin cậy
```

Lưu ý: gộp theo (rule, cột/vùng) trước khi noisy-OR để 1.000 dòng của một cột không đẩy S1 lên 1 một cách giả tạo.

### 4.7 Router (bộ định tuyến ba vùng)

| Vùng | Điều kiện | Hành động |
|---|---|---|
| Chắc chắn | S1 >= T_high (mặc định 0.90) | final, không qua LLM |
| Vùng xám | T_low <= S1 < T_high (T_low mặc định 0.30) | Qua eligibility, có thể xuống LLM |
| Sạch/nhiễu | S1 < T_low hoặc không match | final (sạch), trừ khi thuộc nhóm P2/P3 |

Hai ngưỡng là cấu hình, chỉnh bằng số liệu shadow mode. Mục tiêu tỷ lệ escalate 2 đến 10 phần trăm; đo và chỉnh ngưỡng để đạt mức này.

Lưu ý quan trọng: "điểm thấp" có hai nghĩa (không match gì so với match nhưng không chắc). Chỉ nhóm thứ hai (vùng xám) mặc định đi xuống LLM; nhóm "không match" chỉ đi xuống nếu thuộc P2 hoặc P3.

### 4.8 Eligibility filter và hàng đợi ưu tiên

Điều kiện cần (loại bỏ file vô nghĩa):

- Loại trích được text, sau trích có tối thiểu ~200 ký tự.
- Không thuộc đường dẫn loại trừ; không phải file `needs_ocr`.
- Chưa có kết quả trong `llm_cache` cho cùng `pack_hash` (kiểm tra ở bước đóng gói).
- `file_hash` chưa được xử lý ở cùng `model_ver` và `prompt_ver`.

Ba nhóm ưu tiên:

| Ưu tiên | Nhóm | Ghi chú |
|---|---|---|
| P1 | Có match vùng xám | Giá trị cao nhất |
| P2 | Không match nhưng tên file/đường dẫn/tiêu đề cột đáng nghi | Có hạn ngạch |
| P3 | Lấy mẫu ngẫu nhiên vài phần trăm file sạch | Đo false negative của regex; bỏ đầu tiên khi quá tải |

- Mỗi nhóm có hạn ngạch theo giờ/ngày (cấu hình). Khi quá tải: bỏ P3, rồi P2.
- Tín hiệu suspicion cho P2: từ khóa trong tên file/thư mục (luong, hopdong, nhansu, password, backup), tiêu đề cột nhạy cảm, thư mục của phòng ban nhạy cảm, mật độ chuỗi số bất thường.

### 4.9 Chiến lược trích: một lần hay hai lần

| Phương án | Mô tả | Khi nào chọn |
|---|---|---|
| A. Lazy, trích lần hai (mặc định) | Bảng job chỉ giữ tham chiếu; khi tới lượt thì trích lại đúng file, chọn chunk, mask | Bắt đầu bằng cách này: đơn giản, không lưu snippet, chi phí thêm chỉ áp dụng cho 2 đến 10 phần trăm file |
| B. Trích một lần | Pass 1 thu thập luôn gói ứng viên (ring buffer, reservoir sampling, bộ thu thập cấu trúc), mask ngay, lưu gói nhỏ | Chỉ chuyển sang nếu đo được trích là điểm nghẽn; cần chính sách chấp nhận lưu snippet đã mask kép |

Nếu chọn B: gói (5 đến 20 KB) lưu ở bảng riêng, mã hóa bằng DPAPI, gắn `file_hash`; kèm cờ `needs_reextract` làm đường dự phòng khi gói thiếu hoặc quá cũ.

### 4.10 Context builder: chọn chunk

Ngân sách đầu vào: 1.500 đến 3.000 token mỗi file (ước lượng token bằng số ký tự chia khoảng 3 đối với tiếng Việt, chừa dư).

Thứ tự chọn (dừng khi hết ngân sách):

1. Metadata và cấu trúc: tên file, tiêu đề tài liệu, tên sheet, tên cột (15 phần trăm ngân sách).
2. Ngữ cảnh quanh match vùng xám: cửa sổ ±150 đến 300 ký tự, tối đa 3 đến 5 đoạn, khác rule và khác vị trí (50 phần trăm).
3. Mẫu đại diện: bảng lấy 5 đến 10 dòng đầu và vài dòng ngẫu nhiên; văn bản lấy đoạn đầu, giữa, cuối (35 phần trăm).
4. Phần còn lại không gửi; thay bằng thống kê (số match theo rule, mật độ).

File nhỏ hơn ngân sách thì gửi toàn bộ text đã mask. File nhiều vùng (nhiều sheet) thì xử lý theo đơn vị con rồi gộp kết quả.

### 4.11 Masker (hai lớp)

- Lớp 1: thay từng match của các rule bằng token giữ hình dạng: `<NUM12>`, `<EMAIL>`, `<PHONE>`, `<CARD>`, `<MONEY>`, `<DATE>`. Secret thì `<SECRET len=40 entropy=high>` kèm ngữ cảnh chung quanh (`api_key=`), không gửi giá trị.
- Lớp 2: quét lại chunk bằng bộ regex "PII rộng" để bắt số hoặc email còn sót.
- Tên người: khó bằng regex; thay bằng `<NAME>` khi đứng cạnh nhãn (Họ tên:), còn lại chấp nhận rủi ro còn sót hoặc rút gọn chữ cái đầu.
- Kiểm tra sau mask: nếu chunk vẫn còn chuỗi khớp rule cứng thì hủy chunk, không gửi.
- Mask chạy sau khi chọn chunk, ngay trước khi gửi, ở Regex service.

### 4.12 Worker điều phối job

- Chỉ chạy khi: máy rảnh (CPU dưới ngưỡng, không có phiên người dùng đang hoạt động hoặc chế độ cho phép), pin không yếu (laptop), circuit breaker đang closed hoặc half-open.
- 1 job đồng thời (cấu hình, mặc định 1).
- Trước khi gửi: kiểm tra `file_hash` chưa đổi. Sau khi nhận: kiểm tra lại lần nữa.
- Deadline mỗi job mặc định 60 giây (chỉnh theo đo đạc thực tế).
- Circuit breaker: sau N lỗi/timeout liên tiếp thì mở (ví dụ 2 phút), sau đó half-open thử GetInfo.

### 4.13 Merger

Xem mục 7.

---

## 5. Hợp đồng giao tiếp (IPC)

### 5.1 Kênh

- Named Pipe làm kênh mặc định (kiểm soát được ai kết nối ở tầng hệ điều hành, không mở port).
- Khung giao tiếp: gRPC over named pipe (.NET 8+) hoặc JSON có tiền tố độ dài. Chọn một, cố định.
- Hợp đồng độc lập transport để sau này chuyển sang LLM tập trung chỉ cần đổi địa chỉ.

### 5.2 Hợp đồng (định dạng proto)

**Đã CHỐT** tại `contracts/inference.proto` (bản đầy đủ, kèm 2 enum `Label`/`EvidenceCode` tập đóng — xem mục 6.5).
`contract_ver = 1`. Enum C# tương ứng 1-1: `DlpService/Llm/ContractVersion.cs`, `Llm/Label.cs`, `Llm/EvidenceCode.cs`.

```proto
syntax = "proto3";

service Inference {
  rpc GetInfo (Empty)        returns (Info);
  rpc Classify (ClassifyReq) returns (ClassifyRes);
  rpc Cancel  (CancelReq)    returns (Empty);
}

message Info {
  bool   ready = 1;
  uint32 contract_ver = 2;
  string model_ver = 3;
  string prompt_ver = 4;
  uint32 max_chunks = 5;
  uint32 max_input_tokens = 6;
  uint32 queue_depth = 7;
  uint64 rss_bytes = 8;
}

message Meta {
  string file_type = 1;
  string file_name = 2;
  string parent_dir = 3;
  string title = 4;
  repeated string columns = 5;
  string size_bucket = 6;
}

message Chunk { string kind = 1; string rule = 2; string text = 3; }  // text đã mask

message Finding {
  string rule_id = 1;
  string category = 2;
  float  score = 3;
  bool   validator_passed = 4;
  uint32 count = 5;
  string location = 6;
  bool   hard_rule = 7;
}

message ClassifyReq {
  string job_id = 1;
  uint32 contract_ver = 2;
  Meta   meta = 3;
  repeated Chunk chunks = 4;
  repeated Finding findings = 5;
  uint32 deadline_ms = 6;
}

message Verdict { string rule_id = 1; bool true_positive = 2; float confidence = 3; }

message ClassifyRes {
  string job_id = 1;
  string model_ver = 2;
  string prompt_ver = 3;
  repeated Label labels = 4;          // tập đóng — enum, không phải string (mục 6.5)
  float  confidence = 5;
  repeated Verdict verdicts = 6;
  repeated EvidenceCode evidence = 7; // tập đóng — enum, không phải string (mục 6.5)
  uint32 latency_ms = 8;
  Status status = 9;
}

enum Status { OK = 0; TIMEOUT = 1; BUSY = 2; BAD_CONTRACT = 3; MODEL_ERROR = 4; NOT_READY = 5; OUTPUT_TOO_LONG = 6; }
// OUTPUT_TOO_LONG (thêm buổi 11): đầu ra bị cắt do chạm trần token mà JSON vẫn chưa hợp lệ. Sinh tham lam nên gửi lại cùng
// nội dung sẽ ra cùng kết quả -> lỗi vĩnh viễn của file đó: Regex service KHÔNG thử lại và KHÔNG tính vào circuit breaker.
```

Lý do đổi `labels`/`evidence` từ `repeated string` sang enum so với bản nháp ban đầu: "tập đóng" cần được ép buộc ở
tầng schema — LLM không thể trả về 1 chuỗi lạ mà hệ thống lỡ chấp nhận; xem `enum Label`/`enum EvidenceCode` đầy đủ
trong `contracts/inference.proto` và mục 6.5.

### 5.3 Quy tắc phiên bản

- `contract_ver` chỉ tăng khi thay đổi phá vỡ tương thích; thêm field tùy chọn thì không tăng.
- Mỗi lần kết nối, Regex service gọi GetInfo. Lệch `contract_ver` thì chuyển sang regex-only và cảnh báo; không gửi request sai schema.
- Giữ tương thích lùi một phiên bản.

### 5.4 Ngữ nghĩa vận hành

- Classify là idempotent theo `job_id`.
- LLM process bận thì trả BUSY ngay (không xếp hàng ngầm); Regex service để job nguyên trạng.
- `deadline_ms` được truyền xuống; LLM process kiểm tra trong vòng sinh token, hết hạn thì dừng và trả TIMEOUT.
- `Cancel(job_id)` khi file đổi hoặc service dừng.

### 5.5 Bảo mật kênh

- Phía server (LLM): tạo pipe với PipeSecurity chỉ cho SID của Regex service (vd `NT SERVICE\RegexSvc` sau khi bật `sc sidtype RegexSvc unrestricted`), từ chối mọi SID khác. Dùng FirstPipeInstance để tránh pipe squatting.
- Phía client (Regex): sau khi kết nối, kiểm tra PID và tài khoản chạy hoặc đường dẫn exe (và chữ ký) của process phía server; không khớp thì ngắt.
- Đặt tên pipe không đoán trước được hoặc kèm GUID cài đặt lưu cấu hình.

---

## 6. LLM process: phân tích chi tiết

### 6.1 Vai trò và ranh giới

- Nhận gói ngữ cảnh đã mask, trả nhãn và verdict theo schema.
- Không có: quyền đọc file dữ liệu, kết nối mạng ra ngoài, tool/hành động, trạng thái lâu dài.
- Chạy bằng tài khoản dịch vụ ảo quyền thấp (vd `NT SERVICE\LlmSvc`), chỉ đọc thư mục model và ghi thư mục log của chính nó.
- Chặn outbound mạng bằng Windows Firewall rule theo exe.

### 6.2 Model và runtime

- Model: Phi-4-mini-instruct, định dạng ONNX INT4 cho CPU (hoặc DirectML nếu có GPU).
- Runtime: ONNX Runtime GenAI (.NET binding hoặc C API).
- Kích thước RAM dự kiến khoảng 2 đến 3 GB (đo thực tế trên máy đích). **Số đo thật (buổi 11, có prefix cache):** working set
  4,4 đến 7,0 GB, private đỉnh khoảng 9,8 GB — sát trần Job Object 10240 MB; prefix thật dài 3168 token (không phải ~1300).
- File model nằm ngoài installer chính (offline): gói kèm `manifest.json` chứa SHA-256; LLM process kiểm tra hash sau khi khởi động (xem 6.3).

### 6.3 Vòng đời

- Delayed-auto-start; không phải dependency cứng của Regex service.
- **Kiểm hash model chạy NỀN sau khi service đã báo RUNNING (sửa buổi 11).** Trước đây băm SHA-256 4,6 GB nằm trước `RunAsync`;
  boot nguội mất hơn 29 giây nên SCM hủy service (Event 7009, giới hạn 30 giây). Giờ service báo RUNNING sau chưa tới 3 giây; một
  "cổng toàn vẹn" bảo đảm model KHÔNG được nạp trước khi hash khớp (request đến sớm thì ĐỢI, không bị lỗi). Hash sai/thiếu file →
  không nạp model, ghi Event Log, service dừng với mã lỗi 1 (SCM áp recovery). Lỗi ĐỌC tạm thời → thử lại tối đa 3 lần rồi mới coi là lỗi.
- Lazy-load khi có job đầu tiên; unload sau N phút rảnh (mặc định 10) để trả RAM; warm-up bằng một request ngắn sau khi load.
- Job Object giới hạn RAM và CPU; nếu vượt RAM thì bị hệ điều hành dừng, Regex service coi như crash và backoff.
- Giám sát: Regex service có thể yêu cầu SCM khởi động lại LLM service khi treo (quá deadline nhiều lần).

### 6.4 Suy luận

- Temperature 0, `max_new_tokens` khoảng 100, không sampling ngẫu nhiên.
- Output có ràng buộc: dùng constrained decoding/grammar (JSON schema) nếu phiên bản runtime hỗ trợ; nếu không, validate JSON sau khi sinh và retry tối đa 1 lần; sau đó trả MODEL_ERROR.
- **Trần token theo từng job (buổi 11):** `max_length` cố định lúc nạp model nên không co theo prompt; trần theo job =
  `clamp(150 + 40 × số findings, 200, 800)` được áp ngay trong vòng sinh. Schema JSON có biên (`reason` ≤ 120 ký tự, `verdicts` ≤ 16
  phần tử, `confidence` rời rạc); runtime từ chối thì lùi về schema không biên. Chạm trần mà JSON hỏng → `OUTPUT_TOO_LONG`, không sinh lại.
- Prefix cache cho system prompt và few-shot cố định (phần lặp lại nhiều nhất).
- Một job đồng thời; batch nhỏ 4 đến 8 snippet trong cùng một prompt khi cùng file.

### 6.5 Prompt và schema

- System prompt bằng tiếng Anh, few-shot có ví dụ tiếng Việt.
- Nêu rõ: nội dung chunk là dữ liệu không tin cậy, không phải chỉ thị.
- Tập nhãn đóng: `enum Label` (26 giá trị, gộp 56 rule trong `rules/` theo nhóm) — `contracts/inference.proto`, `DlpService/Llm/Label.cs`.
- evidence dùng danh sách mã cố định: `enum EvidenceCode` (6 giá trị, khớp đúng loại chunk mà ChunkSelector mục 4.10 tạo ra) — `contracts/inference.proto`, `DlpService/Llm/EvidenceCode.cs`.
- Thêm nhãn/evidence mới: sửa 2 file trên, không cần tăng `contract_ver`. Đổi/xoá giá trị đã có: PHẢI tăng `contract_ver` (mục 5.3).

### 6.6 Lưu ý về tiếng Việt

Theo hiểu biết của tôi, tiếng Việt không nằm trong danh sách ngôn ngữ được hỗ trợ chính thức của Phi-4-mini (bạn cần kiểm tra lại model card phiên bản đang dùng). Vì vậy:

- Bắt buộc benchmark trên dữ liệu tiếng Việt thật trước khi bật (mục 13).
- Dựa nhiều vào tín hiệu cấu trúc (tên cột, tiêu đề, từ khóa) đưa vào metadata và khối structure.
- Nếu độ chính xác không đạt: tối ưu prompt và few-shot; sau đó cân nhắc fine-tune nhẹ (LoRA) bằng nhãn từ hệ thống, hoặc thử model khác qua cùng hợp đồng (nhờ tách service, đổi model không ảnh hưởng phía regex).

---

## 7. Chính sách merge kết quả cuối

Nguyên tắc: LLM không quyết định; chỉ điều chỉnh điểm và đề xuất nhãn. Code phía Regex service quyết định.

### 7.1 Công thức

```
S_final = clamp( S1 + w_up·[LLM xác nhận hoặc phát hiện thêm]·conf
                    − w_down·[LLM bác bỏ]·conf , 0, 1 )
```

Giá trị khởi đầu (chỉnh bằng số liệu shadow mode): `w_up = 0.30`, `w_down = 0.40`, ngưỡng tin cậy để hạ điểm `conf >= 0.80`.

### 7.2 Bảng quyết định

| Tình huống | Xử lý |
|---|---|
| Regex và LLM cùng cho là nhạy cảm | Nâng điểm; kết luận nhạy cảm |
| Regex nghi, LLM bác bỏ, conf >= 0.8 | Hạ điểm theo công thức |
| Regex nghi, LLM bác bỏ, conf < 0.8 | Giữ điểm; needs_review=1 |
| Rule hard_rule=true (Luhn, checksum đúng) | LLM không được bác bỏ |
| Rule có llm_policy: never | Bỏ qua kết quả LLM cho rule đó |
| Regex bỏ sót, LLM phát hiện | source=llm, needs_review=1; không tự áp policy nặng (chặn, mã hóa) |
| Timeout, lỗi, LLM tắt | Giữ điểm regex; status=final, needs_review=1 nếu thuộc P1 |
| File đổi trong lúc chờ (file_hash khác) | Bỏ kết quả LLM; quét lại từ Pass 1 |

### 7.3 Gộp nhiều chunk và nhiều verdict

- Nhãn: hợp các nhãn của các chunk.
- Confidence tổng: max hoặc trung bình có trọng số theo độ dài chunk (chọn một, cố định).
- Verdict theo rule: nếu các chunk mâu thuẫn thì coi là không chắc và không hạ điểm.

### 7.4 Lưu vết

Mỗi kết quả lưu đủ S1, S2, S_final, source, model_ver, prompt_ver, rules_ver, merge_policy_ver để truy vết và so sánh khi thay đổi.

---

## 8. Bảo mật và quyền riêng tư

| Mối đe dọa | Biện pháp |
|---|---|
| Dữ liệu thật lọt vào LLM | Mask hai lớp ở Regex service; hủy chunk còn dính rule cứng; LLM process không có quyền đọc file |
| Rò rỉ qua log | Chỉ log job_id, hash, nhãn, mã lỗi; không log nội dung chunk |
| Prompt injection từ nội dung file | Chunk đặt trong khối dữ liệu có delimiter; output chỉ JSON theo schema; LLM không có tool/hành động; loại chuỗi giống chỉ thị ở bước chuẩn hóa; quyết định cuối ở code |
| Process giả mạo nhận snippet | ACL pipe theo SID; FirstPipeInstance; client kiểm tra PID/chữ ký server |
| Khai thác runtime native (ONNX) | Process riêng, tài khoản quyền thấp, Job Object, chặn mạng ra ngoài |
| Model bị thay | Kiểm tra SHA-256 khi khởi động; manifest có chữ ký nếu có thể |
| DB bị đọc | ACL thư mục dữ liệu chỉ cho Regex service; không lưu snippet; nếu chọn phương án B thì mã hóa DPAPI |
| Rule/cấu hình bị sửa | Cấu hình đặt trong thư mục có ACL chặt; ghi hash rule vào audit |
| ReDoS | NonBacktracking hoặc timeout mỗi regex |
| File độc hại (zip bomb...) | Giới hạn giải nén; timeout; process con cho file lạ |

Quyền riêng tư: mọi xử lý cục bộ, không gửi dữ liệu ra ngoài; có tài liệu thông báo cho người dùng/chính sách nội bộ về việc quét và phân loại.

---

## 9. Hiệu năng, throttling, quản lý tài nguyên

- Regex tier: mục tiêu không làm người dùng cảm nhận được (priority thấp, IO thấp, giới hạn song song).
- LLM tier (LLM chậm là ràng buộc chính):
  - Ước lượng thô: CPU 8 nhân, INT4, sinh khoảng 10 đến 20 token/giây, prefill vài trăm token/giây, mỗi file vài giây đến hơn chục giây. Phải đo trên máy đích.
  - Nếu quá chậm thì thắt chặt bộ lọc eligibility, không tăng số worker.
- Điều kiện chạy: CPU dưới ngưỡng (mặc định 40 phần trăm trong 30 giây gần nhất), không có người dùng đang thao tác (idle time), không ở chế độ tiết kiệm pin.
- Tạm dừng ngay khi điều kiện mất; job đang chạy thì hoàn tất hoặc hủy theo deadline.
- Cache hai tầng: theo `file_hash` và theo `pack_hash` (gói đã mask).
- Hạn ngạch theo giờ/ngày cho P2, P3; khống chế kích thước hàng đợi (vượt thì bỏ bớt P3 rồi P2).
- Ngân sách token cố định; batch nhỏ; prefix cache cho phần prompt cố định.

---

## 10. Xử lý lỗi và thoái hóa có kiểm soát

| Tình huống | Phát hiện | Xử lý |
|---|---|---|
| LLM process chưa chạy | Kết nối pipe thất bại | Circuit open; job giữ pending; regex-only; cảnh báo |
| Model đang load | GetInfo.ready=false | Chờ; không tính vào số lần thất bại |
| Crash giữa chừng | Pipe đứt | Job về pending; retry backoff |
| Treo | Quá deadline | Hủy kết nối; timeout; có thể yêu cầu SCM restart LLM service |
| Output không parse được | MODEL_ERROR | Retry tối đa 1 lần; rồi failed + needs_review; giữ kết quả regex |
| Lệch contract_ver | GetInfo | Regex-only + cảnh báo |
| Hết RAM | LLM tự báo hoặc bị Job Object dừng | Xem như crash; tăng backoff |
| File đổi giữa chừng | So file_hash | Job stale; quét lại từ Pass 1 |
| Khởi động sai thứ tự | Kết nối lần đầu thất bại | Không coi là lỗi; retry có backoff |
| Đĩa đầy, DB khóa | Lỗi SQLite | Ghi cảnh báo; tạm dừng Pass 2; luồng regex tiếp tục nếu ghi được |

Nguyên tắc: LLM lỗi thì hệ thống chạy như phiên bản chỉ có regex; không có lỗi LLM nào được làm dừng luồng quét.

---

## 11. Cấu hình

Ví dụ `appsettings.json` (rút gọn; chú thích `//` chỉ để minh họa, loại bỏ nếu parser không hỗ trợ):

```jsonc
{
  "Scan": { "MaxParallelFiles": 4, "MaxReadMb": 20, "ExtractTimeoutSec": 30 },
  "Score": { "THigh": 0.90, "TLow": 0.30 },
  "Llm": {
    "Enabled": true,
    "Mode": "shadow",                 // off | shadow | reduce_fp | full
    "PipeName": "cls-llm-<installGuid>",
    "MaxConcurrent": 1,
    "DeadlineMs": 60000,
    "MaxChunks": 5,
    "InputTokenBudget": 3000,
    "Quota": { "P1PerHour": 200, "P2PerDay": 300, "P3PerDay": 50 },
    "IdleOnly": true,
    "CpuThresholdPct": 40,
    "Breaker": { "FailuresToOpen": 3, "OpenSeconds": 120 }
  },
  "Merge": { "WUp": 0.30, "WDown": 0.40, "MinConfToLower": 0.80, "Policy": "v1" },
  "Privacy": { "StoreSnippets": false, "LogChunkContent": false }
}
```

`Llm.Mode` là công tắc rollout: `off` (chỉ regex), `shadow` (LLM chạy nhưng không đổi kết quả), `reduce_fp` (chỉ cho phép hạ điểm), `full` (cho phép cả nâng điểm với needs_review).

---

## 12. Quan sát và vận hành (observability)

Metric (xuất qua Event Log/ETW/EventCounters, tổng hợp một chỉ báo LLM layer: on / degraded / off):

- Regex service: file/giây, tỷ lệ escalate (theo P1/P2/P3), độ dài hàng đợi, tỷ lệ cache hit, tỷ lệ LLM đổi quyết định so với regex, trạng thái circuit breaker, số job stale/failed/timeout.
- LLM process: latency p50/p95, token/giây, RAM, số lần load/unload, tỷ lệ lỗi parse, thời gian load model.
- Chất lượng (từ shadow và mẫu nhãn tay): precision/recall trước và sau LLM, số ca LLM sửa đúng, số ca LLM sửa sai.

Log: có cấu trúc, chỉ job_id, hash, mã, nhãn. Có trang trạng thái/lệnh CLI chẩn đoán (status, queue, `explain <file_id>` trả điểm S1, S2, lý do merge).

Cảnh báo cần có: hàng đợi tăng liên tục, circuit open kéo dài, RAM LLM vượt ngưỡng, hash model sai, lệch contract.

---

## 13. Kiểm thử và đánh giá chất lượng

### 13.1 Bộ dữ liệu nhãn

- 500 đến 2.000 mẫu, có nhãn người, gồm tiếng Việt và tiếng Anh, đủ loại file chính.
- Có hard negative: mã đơn 12 số, số điện thoại giả, chuỗi hex, ID hệ thống, dữ liệu mẫu (123456789012).
- Có hard positive: dữ liệu nhạy cảm không theo định dạng thường, ngữ cảnh chỉ nằm ở tên cột.
- Chia tập: dev (chỉnh prompt/ngưỡng) và test (không chạm) để tránh overfit.

### 13.2 Loại kiểm thử

| Loại | Nội dung |
|---|---|
| Unit | Rule, validator, scoring, masker (bắt buộc: test mask không để lọt), merger (bảng quyết định), chunk selector (ngân sách token) |
| Property/Fuzz | Extractor với file hỏng, zip bomb, encoding lạ; regex với chuỗi rất dài (ReDoS) |
| Contract | Client và server pipe với contract_ver khác nhau; fake LLM trả từng loại status |
| Tích hợp | Luồng đầy đủ với LLM giả rồi LLM thật; restart giữa chừng; file đổi giữa chừng |
| Chịu lỗi | Kill LLM process, giả treo, giả đĩa đầy, giả DB khóa; kiểm tra thoái hóa về regex-only |
| Bảo mật | ACL pipe (tài khoản khác không kết nối được), pipe squatting, prompt injection trong file mẫu, kiểm tra không có snippet trong log/DB |
| Hiệu năng | Đo thời gian trích, prefill, sinh; đo tác động CPU/RAM tới người dùng; đo trên cấu hình máy thấp nhất được hỗ trợ |

### 13.3 Chỉ số và ngưỡng chấp nhận (đề xuất, chỉnh theo nghiệp vụ)

- Precision của nhãn nhạy cảm sau LLM cao hơn baseline regex-only trên tập test.
- Recall không giảm quá X điểm so với baseline (đặt X theo chính sách rủi ro).
- Tỷ lệ escalate trong khoảng mục tiêu; p95 độ trễ Pass 2 chấp nhận được (định nghĩa theo chính sách, ví dụ hoàn tất trong 24 giờ).
- Tỷ lệ lỗi parse JSON dưới ngưỡng (ví dụ dưới 1 phần trăm sau retry).
- Không có dữ liệu thật trong log/DB/gói gửi LLM (kiểm tra bằng bộ dữ liệu có "canary" giá trị đánh dấu).

### 13.4 Shadow mode

LLM chạy thật nhưng không đổi s_final; ghi s2, verdict để so sánh. Thời gian tối thiểu đủ để có mẫu (ví dụ 2 tuần trên nhóm máy thử nghiệm). Đầu ra: báo cáo LLM sửa đúng/sai bao nhiêu, đề xuất ngưỡng T_low, T_high, w_up, w_down.

---

## 14. Đóng gói và triển khai

- Một installer (MSI hoặc tương đương), thành phần: Regex service, LLM service (tùy chọn), tệp cấu hình, rule.
- Model tải riêng (offline: gói phân phối nội bộ), kiểm tra SHA-256, đặt ở thư mục `ProgramData\<App>\models`, ACL chỉ đọc cho tài khoản LLM.
- Tài khoản dịch vụ: Regex service (đủ quyền đọc thư mục cần quét), LLM service (`NT SERVICE\LlmSvc`, quyền tối thiểu).
- Đăng ký dịch vụ: `sc create`, `sc sidtype ... unrestricted`, thiết lập recovery (restart khi lỗi), delayed-auto-start cho LLM service.
- Firewall: chặn outbound của exe LLM service.
- Cập nhật: LLM service (model/prompt/runtime) cập nhật độc lập; đổi contract thì cập nhật cả hai; giữ tương thích lùi một phiên bản.
- Gỡ cài đặt: giữ hay xóa dữ liệu DB theo lựa chọn; xóa model.
- Yêu cầu tối thiểu: RAM đề xuất cho chế độ có LLM (ví dụ 8 GB, chốt sau khi đo), dung lượng đĩa cho model; máy không đạt thì tự để `Llm.Mode=off`.

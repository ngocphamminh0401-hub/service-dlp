// [Thay đổi] File mới (Bước 2.2): chương trình dòng lệnh chạy TikaExtractor trên JVM thường.
// Dùng 2 việc: (1) kiểm tra kết quả đúng với Tika Server trên bộ file test; (2) sau này làm "bài tập mẫu" cho tracing agent
// của GraalVM sinh cấu hình native-image.
// Cách dùng: ExtractCli <thư-mục-corpus> <file-danh-sách> <thư-mục-ra>
//   file-danh-sách: mỗi dòng 1 đường dẫn tương đối so với thư-mục-corpus, THEO ĐÚNG THỨ TỰ trong manifest.json
//   kết quả: <ra>/rows.jsonl (cùng khuôn với DlpTikaBench) và <ra>/texts/<chỉ-số>.txt
package dlp.tika;

import java.io.BufferedWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

public final class ExtractCli {

    // [Thay đổi] Liệt kê parser/detector Tika đang nạp: dùng để so bản JVM với bản native (ServiceLoader trong native có thể nạp khác)
    private static void listParsers() {
        org.apache.tika.parser.AutoDetectParser parser = new org.apache.tika.parser.AutoDetectParser();
        org.apache.tika.parser.ParseContext ctx = new org.apache.tika.parser.ParseContext();
        java.util.List<org.apache.tika.parser.Parser> flat = new java.util.ArrayList<>();
        flatten(parser, flat);
        java.util.TreeSet<String> lines = new java.util.TreeSet<>();
        for (org.apache.tika.parser.Parser p : flat) {
            lines.add(p.getClass().getName() + "  [" + p.getSupportedTypes(ctx).size() + " loại]");
        }
        System.out.println("PARSERS " + lines.size());
        lines.forEach(l -> System.out.println("  " + l));
        System.out.println("LOAI_MIME_HO_TRO " + parser.getSupportedTypes(ctx).size());
        java.util.TreeSet<String> det = new java.util.TreeSet<>();
        org.apache.tika.detect.Detector d = parser.getDetector();
        if (d instanceof org.apache.tika.detect.CompositeDetector cd) {
            cd.getDetectors().forEach(x -> det.add(x.getClass().getName()));
        } else {
            det.add(d.getClass().getName());
        }
        System.out.println("DETECTORS " + det.size());
        det.forEach(l -> System.out.println("  " + l));
    }

    private static void flatten(org.apache.tika.parser.Parser p, java.util.List<org.apache.tika.parser.Parser> out) {
        if (p instanceof org.apache.tika.parser.CompositeParser cp) {
            for (org.apache.tika.parser.Parser child : cp.getAllComponentParsers()) {
                flatten(child, out);
            }
        } else {
            out.add(p);
        }
    }

    public static void main(String[] args) throws Exception {
        if (args.length == 1 && args[0].equals("--list-parsers")) {
            listParsers();
            return;
        }
        if (args.length < 3) {
            System.err.println("Cách dùng: ExtractCli <thư-mục-corpus> <file-danh-sách> <thư-mục-ra>");
            System.exit(1);
        }
        Path corpus = Path.of(args[0]);
        List<String> files = Files.readAllLines(Path.of(args[1]), StandardCharsets.UTF_8);
        Path out = Path.of(args[2]);
        Files.createDirectories(out.resolve("texts"));

        TikaExtractor extractor = new TikaExtractor();
        int ok = 0, fail = 0;
        try (BufferedWriter rows = Files.newBufferedWriter(out.resolve("rows.jsonl"), StandardCharsets.UTF_8)) {
            for (int i = 0; i < files.size(); i++) {
                String rel = files.get(i).trim();
                if (rel.isEmpty()) {
                    continue;
                }
                long t0 = System.nanoTime();
                TikaExtractor.Result r = extractor.extract(corpus.resolve(rel));
                double ms = (System.nanoTime() - t0) / 1_000_000.0;

                // Quy ước giống kết quả Tika Server trong benchmark: "rỗng" vẫn tính là Ok (nhưng Chars = 0)
                boolean success = r.status() == TikaExtractor.Status.OK || r.status() == TikaExtractor.Status.EMPTY;
                String outcome = success ? "ok" : "fail";
                if (success) {
                    ok++;
                } else {
                    fail++;
                }
                if (r.status() == TikaExtractor.Status.OK) {
                    Files.writeString(out.resolve("texts").resolve(i + ".txt"), r.text(), StandardCharsets.UTF_8);
                }
                rows.write(String.format(java.util.Locale.ROOT,
                        "{\"Index\":%d,\"File\":%s,\"Ok\":%b,\"Ms\":%.3f,\"Chars\":%d,\"Error\":%s,\"Outcome\":%s}%n",
                        i, quote(rel), success, ms, r.text().length(),
                        success && r.detail() == null ? "null" : quote(r.status() + ": " + r.detail()), quote(outcome)));
            }
        }
        System.out.printf("Xong: %d file, %d thành công (gồm rỗng), %d lỗi. Kết quả ở %s%n", files.size(), ok, fail, out);
    }

    // Thoát ký tự tối thiểu để dòng JSON hợp lệ
    private static String quote(String s) {
        return "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\"";
    }
}

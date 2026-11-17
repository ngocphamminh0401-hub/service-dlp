// [Thay đổi] File mới (M6): bộ email mẫu tự dựng cho characterization test của EmlExtractor. Mỗi mẫu nhắm 1 nhánh của bộ phân tích MIME:
// xuống dòng CRLF/LF, multipart lồng nhau, base64 / quoted-printable, header RFC 2047, nhiều bảng mã, thiếu "--boundary--" cuối, attachment.
using System.Text;

namespace DlpService.Tests.Reading;

internal static class EmlSamples
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static string B64(string text, Encoding? enc = null) => Convert.ToBase64String((enc ?? Encoding.UTF8).GetBytes(text), Base64FormattingOptions.InsertLineBreaks);
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks);

    // Ghép các đoạn đã ở dạng byte (để chèn byte windows-1258 / iso-8859-1 thật vào email) — chuỗi Latin-1 giữ nguyên từng byte
    private static byte[] Bytes(params object[] pieces)
    {
        var ms = new MemoryStream();
        foreach (var p in pieces)
        {
            byte[] b = p is byte[] raw ? raw : Latin1.GetBytes((string)p);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Tên mẫu → byte của file .eml.</summary>
    public static IReadOnlyDictionary<string, byte[]> All()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var w1258 = Encoding.GetEncoding(1258);
        var rng = new Random(3);
        byte[] fakePdf = new byte[3000];
        rng.NextBytes(fakePdf);

        return new Dictionary<string, byte[]>
        {
            ["plain"] = Utf8("From: An <an@vd.vn>\r\nTo: Binh <binh@vd.vn>\r\nSubject: Bao cao\r\nDate: Mon, 16 Nov 2026 08:00:00 +0700\r\n" +
                             "Message-ID: <1@vd.vn>\r\n\r\nDong 1\r\nCCCD 001099012345\r\n"),

            ["lf_only"] = Utf8("From: An <an@vd.vn>\nSubject: LF\n\nDong a\nDong b\n"),

            ["folded_headers"] = Utf8("From: An\r\nTo: a@vd.vn,\r\n b@vd.vn,\r\n\tc@vd.vn\r\nCc: d@vd.vn\r\nBcc: e@vd.vn\r\nSubject: Dai\r\n  tiep\r\n\r\nThan\r\n"),

            ["alternative"] = Utf8("Subject: Alt\r\nContent-Type: multipart/alternative; boundary=\"ALT\"\r\n\r\npreamble\r\n" +
                                   "--ALT\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nChu thuong\r\n" +
                                   "--ALT\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<html><body><p>Chu <b>HTML</b></p>\r\n<div>so 0912345678</div></body></html>\r\n" +
                                   "--ALT--\r\nepilogue\r\n"),

            ["base64_text"] = Utf8("Subject: B64\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
                                   B64("Hợp đồng số 12/2026\nSố tài khoản 0123456789\n") + "\r\n"),

            ["quoted_printable"] = Utf8("Subject: QP\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
                                        "H=E1=BB=A3p =C4=91=E1=BB=93ng d=C3=A0i n=C3=AAn b=E1=BB=8B =\r\nng=E1=BA=AFt m=E1=BB=81m\r\nDong 2 =3D bang\r\n"),

            ["header_rfc2047"] = Utf8("From: =?UTF-8?Q?Nguy=E1=BB=85n_V=C4=83n_A?= <a@vd.vn>\r\nSubject: =?UTF-8?B?" +
                                      Convert.ToBase64String(Encoding.UTF8.GetBytes("Bảng lương tháng 11")) + "?=\r\nTo: =?bad?X?zz?=\r\n\r\nThan\r\n"),

            ["charsets"] = Bytes("Subject: Charsets\r\nContent-Type: multipart/mixed; boundary=CS\r\n\r\n",
                                 "--CS\r\nContent-Type: text/plain; charset=windows-1258\r\nContent-Transfer-Encoding: 8bit\r\n\r\n", w1258.GetBytes("Tiếng Việt windows-1258"), "\r\n",
                                 "--CS\r\nContent-Type: text/plain; charset=iso-8859-1\r\n\r\n", Latin1.GetBytes("café crème"), "\r\n",
                                 "--CS\r\nContent-Type: text/plain; charset=x-khong-ton-tai\r\n\r\n", Utf8("Không rõ bảng mã"), "\r\n",
                                 "--CS--\r\n"),

            ["nested3"] = Utf8("Subject: Nested\r\nContent-Type: multipart/mixed; boundary=L1\r\n\r\n" +
                               "--L1\r\nContent-Type: multipart/alternative; boundary=L2\r\n\r\n" +
                               "--L2\r\nContent-Type: multipart/related; boundary=L3\r\n\r\n" +
                               "--L3\r\nContent-Type: text/plain\r\n\r\nTrong cung\r\n--L3--\r\n" +
                               "--L2\r\nContent-Type: text/html\r\n\r\n<p>Ban HTML</p>\r\n--L2--\r\n" +
                               "--L1--\r\n"),

            ["missing_end_boundary"] = Utf8("Subject: Thieu\r\nContent-Type: multipart/mixed; boundary=MB\r\n\r\n" +
                                            "--MB\r\nContent-Type: text/plain\r\n\r\nPhan 1\r\n" +
                                            "--MB\r\nContent-Type: text/plain\r\n\r\nPhan 2 khong co dong ket thuc\r\n"),

            ["attachment_binary"] = Utf8("Subject: Kem file\r\nContent-Type: multipart/mixed; boundary=AT\r\n\r\n" +
                                         "--AT\r\nContent-Type: text/plain\r\n\r\nXem file dinh kem\r\n" +
                                         "--AT\r\nContent-Type: application/pdf; name=\"hoso.pdf\"\r\nContent-Disposition: attachment; filename=\"hoso.pdf\"\r\n" +
                                         "Content-Transfer-Encoding: base64\r\n\r\n" + B64(fakePdf) + "\r\n--AT--\r\n"),

            ["attachment_text_csv"] = Utf8("Subject: CSV\r\nContent-Type: multipart/mixed; boundary=TC\r\n\r\n" +
                                           "--TC\r\nContent-Type: text/plain\r\n\r\nGui bang\r\n" +
                                           "--TC\r\nContent-Type: text/csv; name=\"ds.csv\"\r\nContent-Disposition: attachment; filename=\"ds.csv\"\r\n\r\nten,cccd\r\nAn,001099012345\r\n" +
                                           "--TC--\r\n"),

            ["inline_image"] = Utf8("Subject: Logo\r\nContent-Type: multipart/related; boundary=IM\r\n\r\n" +
                                    "--IM\r\nContent-Type: text/html\r\n\r\n<p>Chu ky <img src=\"cid:logo\"></p>\r\n" +
                                    "--IM\r\nContent-Type: image/png\r\nContent-ID: <logo>\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
                                    B64([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]) + "\r\n--IM--\r\n"),

            // [Thay đổi] Điểm B: khai boundary nhưng thân không có dòng "--XX" nào
            ["no_marker"] = Utf8("Subject: B\r\nContent-Type: multipart/mixed; boundary=XX\r\n\r\nCCCD 001099012345 giau trong than\r\n"),

            // [Thay đổi] Điểm C: multipart nhưng thiếu tham số boundary
            ["no_boundary_param"] = Utf8("Subject: C\r\nContent-Type: multipart/mixed\r\n\r\nCCCD 001099012345 khong co boundary\r\n"),

            // [Thay đổi] Điểm D: dữ liệu giấu ở preamble và epilogue (trình đọc email không hiển thị)
            ["preamble_epilogue"] = Utf8("Subject: D\r\nContent-Type: multipart/mixed; boundary=PE\r\n\r\nCCCD 001099012345 o preamble\r\n" +
                                         "--PE\r\nContent-Type: text/plain\r\n\r\nThan\r\n--PE--\r\nCCCD 001099012399 o epilogue\r\n"),

            ["forwarded_rfc822"] =Utf8("Subject: Fwd\r\nContent-Type: multipart/mixed; boundary=FW\r\n\r\n" +
                                        "--FW\r\nContent-Type: text/plain\r\n\r\nChuyen tiep\r\n" +
                                        "--FW\r\nContent-Type: message/rfc822\r\n\r\nSubject: Thu goc\r\n\r\nNoi dung thu goc 001099012345\r\n" +
                                        "--FW--\r\n"),
        };
    }
}

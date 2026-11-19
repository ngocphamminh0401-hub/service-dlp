// [Thay đổi] File mới (Vòng 4a, tối ưu B5): TextFileReader.ReadAll/SplitLines sau khi bớt bản sao phải cho KẾT QUẢ GIỐNG HỆT cách cũ
// (StreamReader nhận BOM, thử lại Windows-1258 khi có '�', text.Split(["\r\n", "\n"])).
using System.Text;
using DlpService.Reading.Extractors;

namespace DlpService.Tests.Reading;

public class TextFileReaderTests
{
    private static string OldReadAll(byte[] bytes)
    {
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }
        if (text.Contains('�'))
        {
            text = Encoding.GetEncoding(1258).GetString(bytes);
        }
        return text;
    }

    public static IEnumerable<object[]> Samples()
    {
        var utf8 = new UTF8Encoding(false);
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { utf8.GetBytes("Số CCCD: 012345678901\\nHọ tên: Nguyễn Văn A") };
        yield return new object[] { new UTF8Encoding(true).GetPreamble().Concat(utf8.GetBytes("có BOM UTF-8")).ToArray() };
        yield return new object[] { Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("UTF-16 LE có BOM")).ToArray() };
        yield return new object[] { Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("UTF-16 BE có BOM")).ToArray() };
        yield return new object[] { Encoding.GetEncoding(1258).GetBytes("Tiếng Việt Windows-1258: ĐĂNG KÝ") };
        yield return new object[] { new byte[] { 0x61, 0xC3, 0x28, 0x62, 0xFF, 0x63 } };   // UTF-8 lỗi
        yield return new object[] { new byte[] { 0x61, 0xE1, 0xBB } };                       // chuỗi UTF-8 cụt ở cuối
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ReadAll_GiongCachCu_KhiStreamSeekDuoc(byte[] bytes)
    {
        Assert.Equal(OldReadAll(bytes), TextFileReader.ReadAll(new MemoryStream(bytes)));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ReadAll_GiongCachCu_KhiStreamKhongSeekDuoc(byte[] bytes)
    {
        Assert.Equal(OldReadAll(bytes), TextFileReader.ReadAll(new NonSeekableStream(bytes)));
    }

    [Fact]
    public void ReadAll_FileDaiThemKhiDoc_KhongMatDuLieu()
    {
        // Stream báo Length nhỏ hơn dữ liệu thật (file đang được ghi thêm) → vẫn phải đọc hết
        var bytes = new UTF8Encoding(false).GetBytes(new string('a', 10000) + "cuối");
        Assert.Equal(OldReadAll(bytes), TextFileReader.ReadAll(new UnderReportedLengthStream(bytes, reported: 5000)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("một dòng")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\r\n\r\nb")]
    [InlineData("a\rb\nc")]            // '\r' đứng một mình KHÔNG tách dòng
    [InlineData("a\r\r\nb")]           // "\r\r\n": chỉ "\r\n" cuối là dấu xuống dòng
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\n\n\n")]
    [InlineData("kết thúc bằng \r")]
    public void SplitLines_GiongString_Split(string text)
    {
        Assert.Equal(text.Split(["\r\n", "\n"], StringSplitOptions.None), TextFileReader.SplitLines(text).ToArray());
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnderReportedLengthStream(byte[] data, long reported) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reported;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

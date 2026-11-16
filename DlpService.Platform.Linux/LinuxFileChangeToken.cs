// [Thay đổi] File mới (H2): dấu thay đổi của file trên Linux = thiết bị + inode + ctime, đọc bằng statx.
//  - ctime ("change time"): kernel TỰ đặt = thời điểm hiện tại mỗi khi nội dung đổi HOẶC metadata đổi — kể cả khi chương trình gọi utime để
//    đặt lại mtime (`touch -r`, `cp -p`, `tar x`). Không có system call nào cho người dùng đặt ctime tuỳ ý (muốn làm giả phải có root để đổi
//    đồng hồ hệ thống — ngoài phạm vi).
//  - inode: đổi khi file bị thay bằng file khác cùng tên (lưu kiểu ghi file tạm rồi rename — trình soạn thảo, rsync).
//  - dev: inode chỉ duy nhất trong 1 ổ đĩa.
// Vì sao statx qua syscall(): glibc 2.27 (Ubuntu 18.04) chưa có hàm statx(), còn stat() của glibc < 2.33 là hàm inline (không gọi DllImport
// được) và cấu trúc struct stat khác nhau theo kiến trúc. struct statx thì GIỐNG NHAU trên mọi kiến trúc → khai báo 1 lần. Kernel ≥ 4.11 có statx.
using System.Runtime.InteropServices;

namespace DlpService.Platform.Linux;

/// <summary>Dấu thay đổi Linux: "{dev_major}:{dev_minor}:{inode}:{ctime giây}.{ctime nano giây}".</summary>
public sealed class LinuxFileChangeToken : IFileChangeToken
{
    private const int AT_FDCWD = -100;
    private const int AT_STATX_SYNC_AS_STAT = 0x0000;
    private const uint STATX_INO = 0x00000100;
    private const uint STATX_CTIME = 0x00000080;

    // Số hiệu system call statx theo kiến trúc (arch/x86/entry/syscalls/syscall_64.tbl, include/uapi/asm-generic/unistd.h)
    private static readonly long SysStatx = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 332,
        Architecture.Arm64 => 291,
        _ => -1, // kiến trúc khác: không hỗ trợ → luôn trả null (hành vi cũ)
    };

    // struct statx_timestamp (16 byte)
    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp
    {
        public long Sec;
        public uint Nsec;
        public int Reserved;
    }

    // struct statx (256 byte) — include/uapi/linux/stat.h; Size = 256 chừa đủ phần đuôi dự trữ cho kernel ghi vào
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint Blksize;
        public ulong Attributes;
        public uint Nlink;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Ino;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public StatxTimestamp Atime;
        public StatxTimestamp Btime;
        public StatxTimestamp Ctime;
        public StatxTimestamp Mtime;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DevMajor;
        public uint DevMinor;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buffer);

    /// <inheritdoc/>
    public string? Get(string path)
    {
        if (SysStatx < 0)
        {
            return null;
        }

        try
        {
            if (syscall(SysStatx, AT_FDCWD, path, AT_STATX_SYNC_AS_STAT, STATX_INO | STATX_CTIME, out Statx st) != 0)
            {
                return null; // ENOENT (vừa bị xoá), EACCES, ENOSYS (kernel < 4.11)...
            }

            // Kernel không trả được trường nào (VD filesystem lạ) → không có token, dùng hành vi cũ
            if ((st.Mask & (STATX_INO | STATX_CTIME)) != (STATX_INO | STATX_CTIME))
            {
                return null;
            }

            return $"{st.DevMajor}:{st.DevMinor}:{st.Ino}:{st.Ctime.Sec}.{st.Ctime.Nsec:D9}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

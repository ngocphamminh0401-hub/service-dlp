// [Thay đổi] File mới: đọc DB của service đang chạy (process khác) để theo dõi tiến độ và kiểm tra bất biến.
using Microsoft.Data.Sqlite;

namespace DlpResilience;

/// <summary>Truy vấn nhanh 1 file DB. Mỗi lần mở kết nối mới, không pooling → không giữ file khi service/worker đang ghi.</summary>
internal sealed class Db(string path)
{
    public string Path { get; } = path;

    /// <summary>Đếm/lấy 1 số; DB chưa tồn tại hoặc đang bận → -1 (dùng khi chờ tiến độ).</summary>
    public long TryLong(string sql)
    {
        try
        {
            return Long(sql);
        }
        catch (SqliteException)
        {
            return -1;
        }
    }

    public long Long(string sql) => Convert.ToInt64(Scalar(sql));

    public object? Scalar(string sql)
    {
        if (!File.Exists(Path))
        {
            throw new SqliteException("DB chưa tồn tại", 14);
        }
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Đọc nhiều dòng, mỗi dòng ghép các cột bằng '|'.</summary>
    public List<string> Rows(string sql)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            }
            rows.Add(string.Join("|", values));
        }
        return rows;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return connection;
    }
}

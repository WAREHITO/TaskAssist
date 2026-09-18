using System.Text.Json;

namespace TaskAssist.Core;

public static class PipeProtocol
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T data, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        if (bytes.Length > MaxBytes) throw new RuleException("接続データの容量上限を超えました。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation);
        await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var size = BitConverter.ToInt32(header);
        if (size is < 1 or > MaxBytes) throw new RuleException("接続データの容量が不正です。");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new RuleException("接続データが不正です。");
    }
}

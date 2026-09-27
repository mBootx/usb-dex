using System.Buffers.Binary;
using System.Text;

namespace DexStream.Core.Adb;

/// <summary>
/// The subset of ADB's <c>sync:</c> service needed to push the device agent.
/// </summary>
/// <remarks>
/// The sync service frames every request as a four-byte ASCII id followed by a little-endian
/// <c>uint32</c>. <c>SEND</c> carries <c>"path,mode"</c> as its body, then any number of
/// <c>DATA</c> chunks, then <c>DONE</c> whose argument is the file mtime. The device answers
/// <c>OKAY</c> or <c>FAIL</c> with a message.
/// </remarks>
public sealed class AdbSyncClient
{
    /// <summary>Largest DATA chunk the sync service accepts.</summary>
    public const int MaxChunkSize = 64 * 1024;

    private static readonly byte[] SendId = "SEND"u8.ToArray();
    private static readonly byte[] DataId = "DATA"u8.ToArray();
    private static readonly byte[] DoneId = "DONE"u8.ToArray();
    private static readonly byte[] QuitId = "QUIT"u8.ToArray();

    private readonly AdbConnection _connection;

    public AdbSyncClient(AdbConnection connection) => _connection = connection;

    /// <summary>Pushes <paramref name="content"/> to <paramref name="remotePath"/> on the device.</summary>
    /// <param name="mode">POSIX file mode, for example <c>0644</c> written as <c>0x1A4</c>.</param>
    public async Task PushAsync(
        ReadOnlyMemory<byte> content,
        string remotePath,
        int mode = 0b110_100_100,
        DateTimeOffset? modifiedUtc = null,
        CancellationToken cancellationToken = default)
    {
        await using AdbStream stream = await _connection
            .OpenStreamAsync("sync:", cancellationToken)
            .ConfigureAwait(false);

        string target = $"{remotePath},{mode}";
        await WriteRequestAsync(stream, SendId, Encoding.UTF8.GetBytes(target), cancellationToken)
            .ConfigureAwait(false);

        int offset = 0;
        byte[] chunkHeader = new byte[8];
        DataId.CopyTo(chunkHeader, 0);

        while (offset < content.Length)
        {
            int size = Math.Min(MaxChunkSize, content.Length - offset);
            BinaryPrimitives.WriteInt32LittleEndian(chunkHeader.AsSpan(4, 4), size);
            await stream.WriteAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content.Slice(offset, size), cancellationToken).ConfigureAwait(false);
            offset += size;
        }

        long mtime = (modifiedUtc ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        byte[] done = new byte[8];
        DoneId.CopyTo(done, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4, 4), (uint)mtime);
        await stream.WriteAsync(done, cancellationToken).ConfigureAwait(false);

        await ReadStatusAsync(stream, remotePath, cancellationToken).ConfigureAwait(false);

        byte[] quit = new byte[8];
        QuitId.CopyTo(quit, 0);
        try
        {
            await stream.WriteAsync(quit, cancellationToken).ConfigureAwait(false);
        }
        catch (AdbProtocolException)
        {
            // The device often closes the sync stream as soon as it has acknowledged the file.
        }
    }

    private static async Task WriteRequestAsync(
        AdbStream stream,
        byte[] id,
        byte[] body,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        id.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReadStatusAsync(
        AdbStream stream,
        string remotePath,
        CancellationToken cancellationToken)
    {
        byte[] status = new byte[8];
        await ReadExactAsync(stream, status, cancellationToken).ConfigureAwait(false);

        string id = Encoding.ASCII.GetString(status, 0, 4);
        int length = BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(4, 4));

        if (id == "OKAY")
        {
            return;
        }

        if (id == "FAIL")
        {
            string reason = "unknown error";
            if (length is > 0 and < 64 * 1024)
            {
                byte[] body = new byte[length];
                await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false);
                reason = Encoding.UTF8.GetString(body);
            }

            throw new AdbProtocolException($"The device refused to write '{remotePath}': {reason}");
        }

        throw new AdbProtocolException(
            $"Unexpected sync reply '{id}' while writing '{remotePath}'.");
    }

    private static async Task ReadExactAsync(
        AdbStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"The sync stream ended after {offset} of {buffer.Length} expected bytes.");
            }

            offset += read;
        }
    }
}

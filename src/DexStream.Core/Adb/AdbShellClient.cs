using System.Text;

namespace DexStream.Core.Adb;

/// <summary>Convenience helpers for running one-shot shell commands over an <see cref="AdbConnection"/>.</summary>
public sealed class AdbShellClient
{
    private readonly AdbConnection _connection;

    public AdbShellClient(AdbConnection connection) => _connection = connection;

    /// <summary>
    /// Runs <paramref name="command"/> with the legacy <c>shell:</c> service and returns everything it
    /// printed. The legacy service merges stdout and stderr and gives no exit code, which is all we
    /// need for the property queries DexStream makes.
    /// </summary>
    public async Task<string> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        await using AdbStream stream = await _connection
            .OpenStreamAsync($"shell:{command}", cancellationToken)
            .ConfigureAwait(false);

        var output = new StringBuilder();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return output.ToString();
    }

    /// <summary>Reads a single system property, returning null when it is unset.</summary>
    public async Task<string?> GetPropertyAsync(string name, CancellationToken cancellationToken = default)
    {
        string value = (await RunAsync($"getprop {name}", cancellationToken).ConfigureAwait(false)).Trim();
        return value.Length == 0 ? null : value;
    }
}

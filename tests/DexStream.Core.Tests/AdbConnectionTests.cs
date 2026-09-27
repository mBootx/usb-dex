using System.Text;
using DexStream.Core.Adb;
using Xunit;

namespace DexStream.Core.Tests;

public class AdbConnectionTests
{
    private static readonly AdbConnectionOptions FastOptions = new()
    {
        HandshakeTimeout = TimeSpan.FromSeconds(5),
        AuthorizationTimeout = TimeSpan.FromSeconds(5),
    };

    private static (AdbConnection Connection, FakeAdbDevice Device, CancellationTokenSource Cts) Build(
        Action<FakeAdbDevice>? configure = null,
        bool acceptFirstSignature = true)
    {
        var hostToDevice = new BytePipe();
        var deviceToHost = new BytePipe();
        var device = new FakeAdbDevice(hostToDevice, deviceToHost)
        {
            AcceptFirstSignature = acceptFirstSignature,
        };
        configure?.Invoke(device);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = device.RunAsync(cts.Token);

        var transport = new InMemoryTransport(hostToDevice, deviceToHost);
        return (new AdbConnection(transport, FastOptions), device, cts);
    }

    [Fact]
    public async Task ConnectAsync_CompletesTheHandshakeAndReadsTheBanner()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) = Build();
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

            await connection.ConnectAsync(key, cts.Token);

            Assert.True(connection.IsConnected);
            Assert.Equal(device.Banner, connection.DeviceBanner);
            Assert.True(device.SignatureVerified);
            Assert.False(device.PublicKeyOffered);
        }
    }

    [Fact]
    public async Task ConnectAsync_FallsBackToThePublicKeyAndTheDeviceVerifiesTheSignature()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) =
            Build(acceptFirstSignature: false);
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

            await connection.ConnectAsync(key, cts.Token);

            Assert.True(device.PublicKeyOffered);
            // The device rebuilt the modulus from our packed struct and verified our signature
            // with it, which is what adbd does before showing the trust prompt.
            Assert.True(device.SignatureVerified);
            Assert.True(connection.IsConnected);
        }
    }

    [Fact]
    public async Task MaxPayload_IsTheMinimumOfBothSides()
    {
        var hostToDevice = new BytePipe();
        var deviceToHost = new BytePipe();
        var device = new FakeAdbDevice(hostToDevice, deviceToHost) { MaxPayload = 4096 };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = device.RunAsync(cts.Token);

        await using var connection = new AdbConnection(
            new InMemoryTransport(hostToDevice, deviceToHost),
            new AdbConnectionOptions { MaxPayload = 65536, HandshakeTimeout = TimeSpan.FromSeconds(5) });
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

        await connection.ConnectAsync(key, cts.Token);

        Assert.Equal(4096, connection.MaxPayload);
    }

    [Fact]
    public async Task OpenStreamAsync_ReturnsTheServiceOutput()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) = Build(
            d => d.ShellResponses["shell:echo hello"] = "hello\n");
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");
            await connection.ConnectAsync(key, cts.Token);

            var shell = new AdbShellClient(connection);
            string output = await shell.RunAsync("echo hello", cts.Token);

            Assert.Equal("hello\n", output);
            Assert.Contains("shell:echo hello", device.OpenedServices);
        }
    }

    [Fact]
    public async Task OpenStreamAsync_ThrowsWhenTheDeviceRefusesTheService()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) = Build(
            d => d.RefusedServices.Add("shell:nope"));
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");
            await connection.ConnectAsync(key, cts.Token);

            AdbProtocolException error = await Assert.ThrowsAsync<AdbProtocolException>(
                () => connection.OpenStreamAsync("shell:nope", cts.Token));
            Assert.Contains("refused", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task OpenStreamAsync_UsesDistinctStreamIds()
    {
        (AdbConnection connection, FakeAdbDevice _, CancellationTokenSource cts) = Build();
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");
            await connection.ConnectAsync(key, cts.Token);

            await using AdbStream first = await connection.OpenStreamAsync("sync:", cts.Token);
            await using AdbStream second = await connection.OpenStreamAsync("sync:", cts.Token);

            Assert.NotEqual(first.LocalId, second.LocalId);
            Assert.NotEqual(0u, first.RemoteId);
            Assert.NotEqual(first.RemoteId, second.RemoteId);
        }
    }

    [Fact]
    public async Task SyncClient_PushesTheExactBytes()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) = Build();
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");
            await connection.ConnectAsync(key, cts.Token);

            byte[] content = Encoding.UTF8.GetBytes("dex-agent-payload");
            var sync = new AdbSyncClient(connection);

            await sync.PushAsync(content, "/data/local/tmp/agent.jar", cancellationToken: cts.Token);

            Assert.True(device.PushedFiles.TryGetValue("/data/local/tmp/agent.jar", out byte[]? pushed));
            Assert.Equal(content, pushed);
        }
    }

    [Fact]
    public async Task SyncClient_SplitsLargeFilesIntoChunks()
    {
        (AdbConnection connection, FakeAdbDevice device, CancellationTokenSource cts) = Build();
        await using (connection)
        using (cts)
        {
            using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");
            await connection.ConnectAsync(key, cts.Token);

            // Larger than the 64 KiB sync chunk limit, so the client must split it.
            byte[] content = new byte[(AdbSyncClient.MaxChunkSize * 2) + 1234];
            Random.Shared.NextBytes(content);

            await new AdbSyncClient(connection)
                .PushAsync(content, "/data/local/tmp/big.bin", cancellationToken: cts.Token);

            Assert.Equal(content, device.PushedFiles["/data/local/tmp/big.bin"]);
        }
    }

    [Fact]
    public async Task OpenStreamAsync_ThrowsBeforeConnect()
    {
        var hostToDevice = new BytePipe();
        var deviceToHost = new BytePipe();
        await using var connection = new AdbConnection(new InMemoryTransport(hostToDevice, deviceToHost));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => connection.OpenStreamAsync("shell:id"));
    }
}

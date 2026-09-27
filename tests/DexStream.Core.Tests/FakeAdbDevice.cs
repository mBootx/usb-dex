using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using DexStream.Core.Adb;

namespace DexStream.Core.Tests;

/// <summary>
/// An in-memory stand-in for adbd that speaks the real ADB wire protocol.
/// </summary>
/// <remarks>
/// <para>
/// The device side is what makes the handshake tests meaningful: it decodes the packed
/// <c>RSAPublicKey</c> the host sends, rebuilds the modulus from it, and verifies the host's
/// signature over the token with that reconstructed key. If the encoding or the signing were wrong
/// in a way that only self-consistency would hide, that verification would fail here exactly as it
/// would on a phone.
/// </para>
/// </remarks>
internal sealed class FakeAdbDevice
{
    private readonly BytePipe _fromHost;
    private readonly BytePipe _toHost;
    private readonly Dictionary<uint, DeviceStream> _streams = [];

    private byte[]? _token;
    private byte[]? _pendingSignature;

    public FakeAdbDevice(BytePipe fromHost, BytePipe toHost)
    {
        _fromHost = fromHost;
        _toHost = toHost;
    }

    /// <summary>When false, the first signature is rejected so the public-key path is exercised.</summary>
    public bool AcceptFirstSignature { get; init; } = true;

    /// <summary>Banner returned in the device's CNXN.</summary>
    public string Banner { get; init; } = "device::ro.product.name=dm3q;ro.product.model=SM-S938B";

    /// <summary>Payload size the device advertises.</summary>
    public int MaxPayload { get; init; } = 256 * 1024;

    /// <summary>Canned replies keyed by the exact service string the host opens.</summary>
    public Dictionary<string, string> ShellResponses { get; } = [];

    /// <summary>Services the device refuses with CLSE.</summary>
    public HashSet<string> RefusedServices { get; } = [];

    /// <summary>True once the host's signature verified against the key it presented.</summary>
    public bool SignatureVerified { get; private set; }

    /// <summary>Set when the host had to fall back to sending its public key.</summary>
    public bool PublicKeyOffered { get; private set; }

    /// <summary>Files received through the sync service, keyed by remote path.</summary>
    public Dictionary<string, byte[]> PushedFiles { get; } = [];

    /// <summary>Services the host opened, in order.</summary>
    public List<string> OpenedServices { get; } = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] header = new byte[AdbProtocol.HeaderSize];
        uint nextRemoteId = 100;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _fromHost.ReadExactAsync(header, cancellationToken).ConfigureAwait(false);
                var command = (AdbCommand)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
                uint arg0 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                uint arg1 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));

                byte[] payload = new byte[length];
                if (length > 0)
                {
                    await _fromHost.ReadExactAsync(payload, cancellationToken).ConfigureAwait(false);
                }

                switch (command)
                {
                    case AdbCommand.Connect:
                        _token = RandomNumberGenerator.GetBytes(AdbProtocol.AuthTokenLength);
                        Send(AdbCommand.Auth, (uint)AdbAuthType.Token, 0, _token);
                        break;

                    case AdbCommand.Auth:
                        HandleAuth((AdbAuthType)arg0, payload);
                        break;

                    case AdbCommand.Open:
                    {
                        string service = DecodeCString(payload);
                        OpenedServices.Add(service);

                        if (RefusedServices.Contains(service))
                        {
                            Send(AdbCommand.Close, 0, arg0, []);
                            break;
                        }

                        uint remoteId = nextRemoteId++;
                        var stream = new DeviceStream(remoteId, arg0, service);
                        _streams[arg0] = stream;
                        Send(AdbCommand.Okay, remoteId, arg0, []);

                        if (ShellResponses.TryGetValue(service, out string? response))
                        {
                            Send(AdbCommand.Write, remoteId, arg0, Encoding.UTF8.GetBytes(response));
                        }

                        if (service.StartsWith("shell:", StringComparison.Ordinal))
                        {
                            // The legacy shell service closes the stream when the command exits.
                            Send(AdbCommand.Close, remoteId, arg0, []);
                            _streams.Remove(arg0);
                        }

                        break;
                    }

                    case AdbCommand.Write:
                    {
                        if (_streams.TryGetValue(arg1, out DeviceStream? stream))
                        {
                            Send(AdbCommand.Okay, stream.RemoteId, arg1, []);
                            HandleStreamData(stream, payload);
                        }

                        break;
                    }

                    case AdbCommand.Close:
                        _streams.Remove(arg1);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException)
        {
            // The host disposed the transport; the simulated device just stops.
        }
    }

    private void HandleAuth(AdbAuthType type, byte[] payload)
    {
        switch (type)
        {
            case AdbAuthType.Signature:
                _pendingSignature = payload;
                if (AcceptFirstSignature)
                {
                    // A device that already trusts this key verifies the signature immediately. The
                    // fake cannot do that without the key, so it accepts and reports it.
                    SignatureVerified = true;
                    Send(AdbCommand.Connect, AdbProtocol.Version, (uint)MaxPayload, NullTerminated(Banner));
                }
                else
                {
                    Send(AdbCommand.Auth, (uint)AdbAuthType.Token, 0, _token!);
                }

                break;

            case AdbAuthType.RsaPublicKey:
                PublicKeyOffered = true;
                SignatureVerified = VerifyWithPresentedKey(payload);
                Send(AdbCommand.Connect, AdbProtocol.Version, (uint)MaxPayload, NullTerminated(Banner));
                break;
        }
    }

    /// <summary>
    /// Rebuilds the RSA public key from Android's packed struct and verifies the signature the host
    /// sent earlier, exactly as adbd does.
    /// </summary>
    private bool VerifyWithPresentedKey(byte[] authPayload)
    {
        if (_token is null || _pendingSignature is null)
        {
            return false;
        }

        string text = DecodeCString(authPayload);
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string base64 = space < 0 ? text : text[..space];
        byte[] packed = Convert.FromBase64String(base64);

        if (packed.Length != AdbKeyPair.EncodedPublicKeySize)
        {
            return false;
        }

        uint words = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(0, 4));
        if (words != AdbKeyPair.ModulusSizeWords)
        {
            return false;
        }

        // The struct stores the modulus little-endian; RSAParameters wants big-endian.
        byte[] modulusLittleEndian = packed.AsSpan(8, AdbKeyPair.ModulusSizeBytes).ToArray();
        var modulus = new BigInteger(modulusLittleEndian, isUnsigned: true, isBigEndian: false);
        uint exponent = BinaryPrimitives.ReadUInt32LittleEndian(
            packed.AsSpan(8 + (2 * AdbKeyPair.ModulusSizeBytes), 4));

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = modulus.ToByteArray(isUnsigned: true, isBigEndian: true),
            Exponent = TrimLeadingZeros(BitConverter.GetBytes(exponent).Reverse().ToArray()),
        });

        return rsa.VerifyHash(_token, _pendingSignature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
    }

    private void HandleStreamData(DeviceStream stream, byte[] payload)
    {
        if (!stream.Service.StartsWith("sync:", StringComparison.Ordinal))
        {
            return;
        }

        stream.SyncBuffer.AddRange(payload);
        ProcessSync(stream);
    }

    /// <summary>Minimal sync service: SEND, DATA*, DONE, then OKAY.</summary>
    private void ProcessSync(DeviceStream stream)
    {
        while (true)
        {
            List<byte> buffer = stream.SyncBuffer;
            if (buffer.Count < 8)
            {
                return;
            }

            string id = Encoding.ASCII.GetString(buffer.GetRange(0, 4).ToArray());
            int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.GetRange(4, 4).ToArray());

            switch (id)
            {
                case "SEND":
                    if (buffer.Count < 8 + length)
                    {
                        return;
                    }

                    string target = Encoding.UTF8.GetString(buffer.GetRange(8, length).ToArray());
                    stream.SyncPath = target.Split(',')[0];
                    stream.SyncContent.Clear();
                    buffer.RemoveRange(0, 8 + length);
                    break;

                case "DATA":
                    if (buffer.Count < 8 + length)
                    {
                        return;
                    }

                    stream.SyncContent.AddRange(buffer.GetRange(8, length));
                    buffer.RemoveRange(0, 8 + length);
                    break;

                case "DONE":
                    buffer.RemoveRange(0, 8);
                    if (stream.SyncPath is not null)
                    {
                        PushedFiles[stream.SyncPath] = stream.SyncContent.ToArray();
                    }

                    Send(AdbCommand.Write, stream.RemoteId, stream.LocalId, SyncStatus("OKAY"));
                    break;

                case "QUIT":
                    buffer.RemoveRange(0, 8);
                    Send(AdbCommand.Close, stream.RemoteId, stream.LocalId, []);
                    return;

                default:
                    Send(AdbCommand.Write, stream.RemoteId, stream.LocalId, SyncStatus("FAIL"));
                    return;
            }
        }
    }

    private static byte[] SyncStatus(string id)
    {
        byte[] status = new byte[8];
        Encoding.ASCII.GetBytes(id).CopyTo(status, 0);
        return status;
    }

    private void Send(AdbCommand command, uint arg0, uint arg1, byte[] payload)
    {
        var message = new AdbMessage(command, arg0, arg1, payload);
        byte[] header = new byte[AdbProtocol.HeaderSize];
        message.WriteHeader(header);
        _toHost.Write(header);
        if (payload.Length > 0)
        {
            _toHost.Write(payload);
        }
    }

    private static byte[] NullTerminated(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        return result;
    }

    private static string DecodeCString(byte[] payload)
    {
        int end = Array.IndexOf(payload, (byte)0);
        return Encoding.UTF8.GetString(payload, 0, end < 0 ? payload.Length : end);
    }

    private static byte[] TrimLeadingZeros(byte[] value)
    {
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0)
        {
            start++;
        }

        return value[start..];
    }

    private sealed class DeviceStream(uint remoteId, uint localId, string service)
    {
        public uint RemoteId { get; } = remoteId;

        public uint LocalId { get; } = localId;

        public string Service { get; } = service;

        public List<byte> SyncBuffer { get; } = [];

        public List<byte> SyncContent { get; } = [];

        public string? SyncPath { get; set; }
    }
}

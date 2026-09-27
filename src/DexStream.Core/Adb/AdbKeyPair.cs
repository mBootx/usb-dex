using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace DexStream.Core.Adb;

/// <summary>
/// The RSA-2048 key pair that identifies this host to adbd, plus the conversion to
/// Android's packed <c>RSAPublicKey</c> wire format.
/// </summary>
/// <remarks>
/// <para>
/// adbd does not accept a standard SubjectPublicKeyInfo blob. It expects the layout from
/// <c>system/core/libcrypto_utils/android_pubkey.c</c>:
/// </para>
/// <code>
/// struct RSAPublicKey {
///     uint32_t modulus_size_words;      // 2048 / 32 == 64
///     uint32_t n0inv;                   // -1 / n[0] mod 2^32
///     uint8_t  modulus[256];            // little-endian
///     uint8_t  rr[256];                 // (2^2048)^2 mod n, little-endian
///     uint32_t exponent;                // 3 or 65537
/// };
/// </code>
/// <para>
/// The 524-byte struct is base64 encoded and sent as
/// <c>"&lt;base64&gt; &lt;user&gt;@&lt;host&gt;\0"</c> in an AUTH(RSAPUBLICKEY) message.
/// </para>
/// </remarks>
public sealed class AdbKeyPair : IDisposable
{
    /// <summary>RSA modulus length in bytes. adbd only supports 2048-bit keys.</summary>
    public const int ModulusSizeBytes = 256;

    /// <summary>Modulus length in 32-bit words, the value stored in the first struct field.</summary>
    public const int ModulusSizeWords = ModulusSizeBytes / 4;

    /// <summary>Size of the encoded <c>RSAPublicKey</c> struct in bytes.</summary>
    public const int EncodedPublicKeySize = 4 + 4 + ModulusSizeBytes + ModulusSizeBytes + 4;

    private readonly RSA _rsa;
    private bool _disposed;

    private AdbKeyPair(RSA rsa, string identity)
    {
        _rsa = rsa;
        Identity = identity;
    }

    /// <summary>The <c>user@host</c> string shown on the device's authorization prompt.</summary>
    public string Identity { get; }

    /// <summary>Generates a fresh 2048-bit key pair.</summary>
    public static AdbKeyPair Generate(string identity)
    {
        var rsa = RSA.Create(2048);
        return new AdbKeyPair(rsa, identity);
    }

    /// <summary>
    /// Loads a private key from PEM text. Accepts both the PKCS#8 <c>PRIVATE KEY</c> form that
    /// modern <c>adb</c> writes to <c>~/.android/adbkey</c> and the PKCS#1 <c>RSA PRIVATE KEY</c> form.
    /// </summary>
    /// <exception cref="AdbProtocolException">The PEM is unreadable or is not a 2048-bit RSA key.</exception>
    public static AdbKeyPair FromPem(string pem, string identity)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new AdbProtocolException(
                "Could not read the ADB private key. Expected a PEM-encoded RSA private key.", ex);
        }

        if (rsa.KeySize != 2048)
        {
            int size = rsa.KeySize;
            rsa.Dispose();
            throw new AdbProtocolException(
                $"ADB requires a 2048-bit RSA key, but the supplied key is {size} bits.");
        }

        return new AdbKeyPair(rsa, identity);
    }

    /// <summary>Exports the private key as an unencrypted PKCS#8 PEM, matching <c>adb</c>'s own format.</summary>
    public string ToPrivateKeyPem() => _rsa.ExportPkcs8PrivateKeyPem();

    /// <summary>
    /// Signs the 20-byte token from an AUTH(TOKEN) message.
    /// </summary>
    /// <remarks>
    /// adbd verifies with <c>RSA_verify(NID_sha1, token, 20, ...)</c>, so the token is treated as an
    /// already-computed SHA-1 digest and wrapped in a PKCS#1 v1.5 DigestInfo. We therefore sign the
    /// hash directly rather than hashing the token again.
    /// </remarks>
    public byte[] SignToken(ReadOnlySpan<byte> token)
    {
        if (token.Length != AdbProtocol.AuthTokenLength)
        {
            throw new ArgumentException(
                $"An ADB auth token is {AdbProtocol.AuthTokenLength} bytes, got {token.Length}.",
                nameof(token));
        }

        return _rsa.SignHash(token.ToArray(), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
    }

    /// <summary>Encodes the public key into Android's packed 524-byte <c>RSAPublicKey</c> struct.</summary>
    public byte[] EncodePublicKey()
    {
        RSAParameters parameters = _rsa.ExportParameters(includePrivateParameters: false);
        return EncodePublicKey(parameters);
    }

    /// <summary>The full AUTH(RSAPUBLICKEY) payload: base64 struct, a space, the identity, and a NUL.</summary>
    public byte[] EncodePublicKeyPayload()
        => AdbMessage.NullTerminated($"{Convert.ToBase64String(EncodePublicKey())} {Identity}");

    /// <summary>
    /// The contents of an <c>adbkey.pub</c> file: the base64 struct, a space and the identity,
    /// with no trailing NUL.
    /// </summary>
    public string ToPublicKeyFile()
        => $"{Convert.ToBase64String(EncodePublicKey())} {Identity}";

    internal static byte[] EncodePublicKey(RSAParameters parameters)
    {
        byte[]? modulusBytes = parameters.Modulus;
        byte[]? exponentBytes = parameters.Exponent;

        if (modulusBytes is null || exponentBytes is null)
        {
            throw new AdbProtocolException("The RSA key is missing its public modulus or exponent.");
        }

        // RSAParameters stores big-endian, unsigned, minimal-length integers.
        BigInteger n = ToPositiveBigInteger(modulusBytes);
        BigInteger e = ToPositiveBigInteger(exponentBytes);

        if (GetByteLength(n) != ModulusSizeBytes)
        {
            throw new AdbProtocolException(
                $"ADB requires a 2048-bit modulus ({ModulusSizeBytes} bytes), got {GetByteLength(n)} bytes.");
        }

        if (e > uint.MaxValue)
        {
            throw new AdbProtocolException(
                $"The RSA public exponent {e} does not fit in the uint32 field of Android's RSAPublicKey struct.");
        }

        byte[] buffer = new byte[EncodedPublicKeySize];
        Span<byte> span = buffer;

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], ModulusSizeWords);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], ComputeN0Inv(n));
        WriteLittleEndianFixed(n, span.Slice(8, ModulusSizeBytes));
        WriteLittleEndianFixed(ComputeRr(n), span.Slice(8 + ModulusSizeBytes, ModulusSizeBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(
            span.Slice(8 + (2 * ModulusSizeBytes), 4), (uint)e);

        return buffer;
    }

    /// <summary>
    /// Computes <c>n0inv = 2^32 - (n mod 2^32)^-1 mod 2^32</c>, the Montgomery constant adbd
    /// uses to avoid a modular inversion on the device.
    /// </summary>
    internal static uint ComputeN0Inv(BigInteger n)
    {
        BigInteger r32 = BigInteger.One << 32;
        BigInteger n0 = n % r32;
        BigInteger inverse = ModInverse(n0, r32);
        return (uint)(r32 - inverse);
    }

    /// <summary>Computes <c>rr = (2^2048)^2 mod n</c>, the Montgomery R-squared value.</summary>
    internal static BigInteger ComputeRr(BigInteger n)
    {
        BigInteger r = BigInteger.One << (ModulusSizeBytes * 8);
        return BigInteger.Remainder(r * r, n);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as a fixed-width little-endian byte array, the word order
    /// Android's struct uses. Values shorter than the field are zero padded on the right.
    /// </summary>
    internal static void WriteLittleEndianFixed(BigInteger value, Span<byte> destination)
    {
        destination.Clear();
        if (!value.TryWriteBytes(destination, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new AdbProtocolException(
                $"Value does not fit in the {destination.Length}-byte field of Android's RSAPublicKey struct.");
        }
    }

    internal static BigInteger ToPositiveBigInteger(byte[] bigEndianBytes)
        => new(bigEndianBytes, isUnsigned: true, isBigEndian: true);

    internal static int GetByteLength(BigInteger value)
        => value.GetByteCount(isUnsigned: true);

    /// <summary>Extended Euclidean modular inverse. <paramref name="modulus"/> must be positive.</summary>
    internal static BigInteger ModInverse(BigInteger value, BigInteger modulus)
    {
        if (modulus <= BigInteger.One)
        {
            throw new ArgumentOutOfRangeException(nameof(modulus), "Modulus must be greater than one.");
        }

        BigInteger a = ((value % modulus) + modulus) % modulus;
        BigInteger oldR = a, r = modulus;
        BigInteger oldS = BigInteger.One, s = BigInteger.Zero;

        while (r != BigInteger.Zero)
        {
            BigInteger quotient = BigInteger.Divide(oldR, r);
            (oldR, r) = (r, oldR - (quotient * r));
            (oldS, s) = (s, oldS - (quotient * s));
        }

        if (oldR != BigInteger.One)
        {
            throw new AdbProtocolException(
                "The RSA modulus is even, so it has no inverse modulo 2^32. The key is malformed.");
        }

        return ((oldS % modulus) + modulus) % modulus;
    }

    /// <summary>
    /// Builds the <c>user@host</c> identity string, sanitised so it cannot inject a space or NUL
    /// into the AUTH payload (both are field separators on the wire).
    /// </summary>
    public static string BuildIdentity(string user, string host)
    {
        static string Clean(string value, string fallback)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
                {
                    sb.Append(c);
                }
            }

            return sb.Length == 0 ? fallback : sb.ToString();
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Clean(user, "dexstream")}@{Clean(host, "windows")}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rsa.Dispose();
    }
}

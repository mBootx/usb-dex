using System.Numerics;
using System.Security.Cryptography;
using DexStream.Core.Adb;
using Xunit;

namespace DexStream.Core.Tests;

public class AdbKeyPairTests
{
    [Fact]
    public void EncodePublicKey_MatchesIndependentImplementation()
    {
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

        byte[] encoded = key.EncodePublicKey();

        Assert.Equal(AdbKeyPair.EncodedPublicKeySize, encoded.Length);
        Assert.Equal(AdbKeyTestVector.ExpectedPackedPublicKeyBase64, Convert.ToBase64String(encoded));
    }

    [Fact]
    public void EncodePublicKey_HasTheStructLayoutAdbdExpects()
    {
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

        byte[] encoded = key.EncodePublicKey();

        Assert.Equal((uint)AdbKeyPair.ModulusSizeWords, BitConverter.ToUInt32(encoded, 0));
        Assert.Equal(AdbKeyTestVector.ExpectedN0Inv, BitConverter.ToUInt32(encoded, 4));
        Assert.Equal(65537u, BitConverter.ToUInt32(encoded, 4 + 4 + (2 * AdbKeyPair.ModulusSizeBytes)));
    }

    [Fact]
    public void ComputeN0Inv_IsTheNegatedInverseModTwoToThe32()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(AdbKeyTestVector.PrivateKeyPem);
        RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);
        BigInteger n = AdbKeyPair.ToPositiveBigInteger(parameters.Modulus!);

        uint n0inv = AdbKeyPair.ComputeN0Inv(n);

        // The whole point of n0inv is that n * n0inv == -1 (mod 2^32).
        BigInteger product = (n * n0inv) % (BigInteger.One << 32);
        Assert.Equal((BigInteger.One << 32) - 1, product);
    }

    [Fact]
    public void ComputeRr_IsRSquaredModN()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(AdbKeyTestVector.PrivateKeyPem);
        BigInteger n = AdbKeyPair.ToPositiveBigInteger(
            rsa.ExportParameters(includePrivateParameters: false).Modulus!);

        BigInteger rr = AdbKeyPair.ComputeRr(n);

        BigInteger r = BigInteger.One << (AdbKeyPair.ModulusSizeBytes * 8);
        Assert.Equal(BigInteger.Remainder(r * r, n), rr);
        Assert.True(rr < n);
    }

    [Fact]
    public void SignToken_MatchesOpenSsl()
    {
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

        byte[] signature = key.SignToken(AdbKeyTestVector.Token);

        Assert.Equal(AdbKeyPair.ModulusSizeBytes, signature.Length);
        Assert.Equal(AdbKeyTestVector.ExpectedSignatureBase64, Convert.ToBase64String(signature));
    }

    [Fact]
    public void SignToken_RejectsWrongLength()
    {
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "test@dexstream");

        Assert.Throws<ArgumentException>(() => key.SignToken(new byte[19]));
    }

    [Fact]
    public void EncodePublicKeyPayload_IsBase64SpaceIdentityNul()
    {
        using var key = AdbKeyPair.FromPem(AdbKeyTestVector.PrivateKeyPem, "bob@desktop");

        byte[] payload = key.EncodePublicKeyPayload();

        Assert.Equal(0, payload[^1]);
        string text = System.Text.Encoding.UTF8.GetString(payload, 0, payload.Length - 1);
        Assert.Equal($"{AdbKeyTestVector.ExpectedPackedPublicKeyBase64} bob@desktop", text);
    }

    [Fact]
    public void RoundTripPem_PreservesTheKey()
    {
        using var original = AdbKeyPair.Generate("a@b");
        string pem = original.ToPrivateKeyPem();

        using var reloaded = AdbKeyPair.FromPem(pem, "a@b");

        Assert.Equal(
            Convert.ToBase64String(original.EncodePublicKey()),
            Convert.ToBase64String(reloaded.EncodePublicKey()));
    }

    [Fact]
    public void FromPem_RejectsKeysThatAreNot2048Bit()
    {
        using var small = RSA.Create(1024);
        string pem = small.ExportPkcs8PrivateKeyPem();

        AdbProtocolException error = Assert.Throws<AdbProtocolException>(
            () => AdbKeyPair.FromPem(pem, "a@b"));
        Assert.Contains("2048", error.Message);
    }

    [Fact]
    public void FromPem_RejectsGarbage()
        => Assert.Throws<AdbProtocolException>(() => AdbKeyPair.FromPem("not a pem", "a@b"));

    [Theory]
    [InlineData("user name", "my host!", "username@myhost")]
    [InlineData("", "", "dexstream@windows")]
    [InlineData("a b\0c", "h\nost", "abc@host")]
    public void BuildIdentity_StripsCharactersThatWouldBreakTheWireFormat(
        string user,
        string host,
        string expected)
        => Assert.Equal(expected, AdbKeyPair.BuildIdentity(user, host));

    [Fact]
    public void ModInverse_AgreesWithBruteForceOnSmallValues()
    {
        for (int m = 3; m < 40; m++)
        {
            for (int v = 1; v < m; v++)
            {
                if (BigInteger.GreatestCommonDivisor(v, m) != 1)
                {
                    continue;
                }

                BigInteger inverse = AdbKeyPair.ModInverse(v, m);
                Assert.Equal(BigInteger.One, (inverse * v) % m);
            }
        }
    }

    [Fact]
    public void WriteLittleEndianFixed_ZeroPadsShortValues()
    {
        Span<byte> buffer = stackalloc byte[8];

        AdbKeyPair.WriteLittleEndianFixed(new BigInteger(0x0102), buffer);

        Assert.Equal(new byte[] { 0x02, 0x01, 0, 0, 0, 0, 0, 0 }, buffer.ToArray());
    }

    [Fact]
    public void WriteLittleEndianFixed_ThrowsWhenTheValueDoesNotFit()
    {
        byte[] buffer = new byte[2];

        Assert.Throws<AdbProtocolException>(
            () => AdbKeyPair.WriteLittleEndianFixed(new BigInteger(0x010203), buffer));
    }
}

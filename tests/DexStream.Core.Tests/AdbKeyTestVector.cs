namespace DexStream.Core.Tests;

/// <summary>
/// A pinned RSA-2048 key together with the values an independent implementation produced for it.
/// </summary>
/// <remarks>
/// <para>
/// The key was generated with OpenSSL. The packed public key and <c>n0inv</c> were then computed by a
/// separate Python implementation of <c>system/core/libcrypto_utils/android_pubkey.c</c>, and the
/// signature by <c>openssl pkeyutl -sign -pkeyopt digest:sha1</c>, which is exactly the
/// <c>RSA_sign(NID_sha1, ...)</c> call adbd verifies against.
/// </para>
/// <para>
/// Checking <see cref="DexStream.Core.Adb.AdbKeyPair"/> against these values means the wire format is
/// validated by a second toolchain rather than by itself. The key is a throwaway test fixture and is
/// not used to authenticate against any device.
/// </para>
/// </remarks>
internal static class AdbKeyTestVector
{
    /// <summary>The private key in the PKCS#8 PEM form that modern <c>adb</c> writes.</summary>
    public const string PrivateKeyPem =
        "-----BEGIN PRIVATE KEY-----" + "\n" +
        "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDJpIbi9d4HulEa" + "\n" +
        "cbPou/CbQ9oOHkmlt/kckGkC+4PRAeouprvfyFXVLuiRQYfyU7XbwzC23CDs8L2w" + "\n" +
        "1F1VkwXWvaNzliEmEO8tIp09IJp8PboAWVrewESJUxfzVMNFNRwa4zQC8wl9dlnn" + "\n" +
        "oS3zg1QgtV+m6PBHwjRj0vAu0bp/GwW/cLtqIFNbwsXIUJ5MQossoiXeHYfov1aI" + "\n" +
        "HnGYff7jOLwz5j4kibfD6dtIkpsT1a/UQNj9Z1PLR3AUoWnfVa+pE07G2i8wf9AC" + "\n" +
        "QPApreCs1Lk0kP5Al4V9dS2DfoK6TnXvgjuOB/OadgQvpKEuSHU6jT7hRO9cYvmV" + "\n" +
        "9aXALOGHAgMBAAECggEANlhYXk0DlTOa7jSc12nntAD6gPmawcYupospMjuVUDxi" + "\n" +
        "4EXhtrNoprcoyRVPTXlQNzWJKI91dEaB6Wmi6hBg73pW1i0tK/Q+cfy1QidKW5fL" + "\n" +
        "TJLIkeZh0l0JxHQg2MMymvCHMRBNi3ndSzc+ijXn85TOfc6kC8MEma7dOtEdoWms" + "\n" +
        "8JN33Ugl3eLp011Huqps+pfBkqeOTE6un3agq+dk8TEI5qBfe9FJdg5ML2rLAuME" + "\n" +
        "SaF8vAgpSpmFN6fQVj768GvnVnEKXeDYYUvePbvsZgyH/2pP7zp4S1Z2hiDhefsq" + "\n" +
        "KeZcbpVp0cP5p2ni2ZUs9xL7S7QRK12cnE3+BV4Q+QKBgQD7SazZYgUwZ/rTewJ9" + "\n" +
        "sKIM8Pc0uwZxt6BLcbKkykqJz3F4HFnCzoRdfHBLApdWAI36v2eoiONJtdSKh+i8" + "\n" +
        "jxtVF38EltOky2CQuwM5EzGchOEjIPrFRQHRMdQ47ZLhYboJe5iJFNx7UStRTv4y" + "\n" +
        "AxqVwXANJAQTve3tQSEinXNKSwKBgQDNbIbfg26AXORBsZlkIl8WuD5tGDuNnvQv" + "\n" +
        "06GOAvidm3ex5nR+Sjn5i0s1qeaJrZQK379IsgN2U0RCTlcxfnF/yEfAslE6kMnC" + "\n" +
        "pV+MxbmOwUBFZD0yr0jlL16p72XcPEO4qLhIpFzmz8uOFlOVzT73qMUJ/OsZ66rr" + "\n" +
        "zxqKXNuANQKBgFfVAaid3Uh1H7PprA44vfueAhoZQQBgeZPFMvbsii8vJe8gobM4" + "\n" +
        "sgVnKGzfg/wYh4fcfSPobOFnv9mH9a5qqtgMNWZqPaG9QIx2AYNilRWrUHIR5fUr" + "\n" +
        "0J3JN++KAqvql7cWz1MiyooD7gsmfC0I7rLngP3m19H6sf4apLVPqWuPAoGBAKkM" + "\n" +
        "YoGVe14PE5gsOvr4fiAIRvcoNfn5kSG6mvxu3YF16y5vY/Z5xvPg+WeUBa/PNEEu" + "\n" +
        "mA+OzuKU61tVdNO6JlTt0H53P/leJWsVBGioXmdoCSgRsXsAXJCeRty5sa1nuqM/" + "\n" +
        "1UeSWxFmdzDalrZ0TRkpvdzWUfLmyaV8rW6ns8rxAoGAV1sdnIVUR8zi7Tc5JaDZ" + "\n" +
        "iNFjQDnQQ+SVDQIqz9Qbzkr2YkvAIZk2688LtKvhJkJxD/NS9RyWI/cd064o+Knr" + "\n" +
        "mC4jjXo+nmfFEXAhIJ2xX+RJcZOV45uwGKlrY3lspqKVKI+so/LZ2wCL5oZ24Py3" + "\n" +
        "9M+lc31Vsgfk28IMMsgNJSU=" + "\n" +
        "-----END PRIVATE KEY-----";

    /// <summary>Expected <c>n0inv</c>: <c>2^32 - (n mod 2^32)^-1 mod 2^32</c>.</summary>
    public const uint ExpectedN0Inv = 3882150857u;

    /// <summary>Expected base64 of the 524-byte packed <c>RSAPublicKey</c> struct.</summary>
    public const string ExpectedPackedPublicKeyBase64 =
        "QAAAAMnrZOeH4SzApfWV+WJc70ThPo06dUguoaQvBHaa8weOO4LvdU66gn6DLXV9hZdA/pA0udSs4K0p8EAC0H8wL9rGThOpr1Xf" +
        "aaEUcEfLU2f92EDUr9UTm5JI2+nDt4kkPuYzvDjj/n2YcR6IVr/ohx3eJaIsi0JMnlDIxcJbUyBqu3C/BRt/utEu8NJjNMJH8Oim" +
        "X7UgVIPzLaHnWXZ9CfMCNOMaHDVFw1TzF1OJRMDeWlkAuj18miA9nSIt7xAmIZZzo73WBZNVXdSwvfDsINy2MMPbtVPyh0GR6C7V" +
        "Vcjfu6Yu6gHRg/sCaZAc+belSR4O2kOb8Lvos3EaUboH3vXihqTJDMJ778tvaJ0B6sobBoJxraw0E0+A7/jm6ND9i6+5NLZAmliE" +
        "MRV3M1MVti7kRcls3c++rUB8FkmcSY3De61tGjXFnWsPoJJkmgDXvuWgBVpErJcH1cDjgf264BEh0ZZwRZgoXoB0DXF0d0ywkT5Y" +
        "EQaqv6Ykth7Wg72CQkEn/rTTkN2iHsxw8NdKcAklUIeiwpkOJ+mmC+rZhRXoMbHeWQQyrm6OdCcS+p2uP2pNeLEq2j1JGn6g4/Ig" +
        "hHlSBuJGlHED9ialaNQwzcr0y/Nk92UmyR8L0NyVowkerC2tbeULoKfsFWWBngU4fZss2gcOlT3NgmPlFvsftRihEvCXjwEAAQA=";

    /// <summary>The 20-byte token that gets signed: 0x11 through 0x24.</summary>
    public static byte[] Token =>
    [
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A,
        0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24,
    ];

    /// <summary>OpenSSL's PKCS#1 v1.5 SHA-1 signature over <see cref="Token"/>.</summary>
    public const string ExpectedSignatureBase64 =
        "r+aYlzf0FoReIJ73KRGOLYP/yxaVJCHXmq7BVJnqsWeJJz1yz2qVqDEt7XzHp5+MuN/fETVvT/kHGDGZtlmsMphqVZrbx/VdSP9y" +
        "Cjau3uitjohSMys5xnZv+pwcSpF7rGxeOwIULGxF1LQGKtKNOx+pSicIdKcVfKfPAHxAhn9gL5i+zbsbD+2mlcRaZv4OFSFP2MX4" +
        "6EMYLeKp29AzBAvbCqaTlF1tF63yJ0x0E+2QRaSM8391YkXfTfvW3Q2PbM0wSemstiDJ+6NdhbUVwq3QUA+ESmy6wku001IOFDu3" +
        "T3ZVQqb6GOpZnOUgiCKbr7CPMxbyI6N7ibYi4eDM4Q==";
}

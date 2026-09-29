using System.Security.Cryptography;
using System.Text;
using Desk.Infrastructure.Notifications;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// Web Push encryption, proven against the worked example in RFC 8291 (§5 and Appendix A). A browser
/// will only decrypt a message built exactly this way, so the test is the standard's own bytes.
/// </summary>
public class WebPushCryptoTests
{
    private static byte[] B(string base64Url) => WebPushCrypto.FromBase64Url(base64Url.Replace(" ", "").Replace("\n", ""));

    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";

    /// <summary>RFC 8291 §5: the complete request body for "When I grow up, I want to be a watermelon".</summary>
    private const string ExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
        "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
        "pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    private static ECDiffieHellman Key(string publicKey, string privateKey)
    {
        var point = B(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    [Fact]
    public void The_rfc_8291_example_encrypts_to_exactly_the_published_body()
    {
        using var sender = Key(AsPublic, AsPrivate);

        var body = WebPushCrypto.Encrypt(B(UaPublic), B(AuthSecret),
            Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"), sender, B(Salt));

        WebPushCrypto.Base64Url(body).Should().Be(ExpectedBody);
        // 86-byte header + 41 bytes of text + delimiter + 16-byte tag. (The example's request line says
        // Content-Length: 145; the body it prints decodes to 144, which is what the arithmetic gives.)
        body.Length.Should().Be(86 + 41 + 1 + 16);
    }

    [Fact]
    public void A_real_message_uses_a_fresh_key_and_salt_every_time_and_the_browser_can_still_read_it()
    {
        var plaintext = Encoding.UTF8.GetBytes("{\"title\":\"INT-000014 assigned to you\"}");

        var one = WebPushCrypto.Encrypt(B(UaPublic), B(AuthSecret), plaintext);
        var two = WebPushCrypto.Encrypt(B(UaPublic), B(AuthSecret), plaintext);

        one.Should().NotEqual(two);
        Decrypt(one).Should().Equal(plaintext);
    }

    [Fact]
    public void The_vapid_header_is_a_valid_es256_token_for_the_push_services_origin()
    {
        var (publicKey, privateKey) = WebPushCrypto.NewVapidKeys();
        using var signing = WebPushCrypto.VapidKey(publicKey, privateKey);
        var now = DateTimeOffset.Parse("2026-09-29T10:00:00Z");

        var header = WebPushCrypto.VapidAuthorization("https://fcm.googleapis.com/fcm/send/abc123", signing, "https://piomanage.com", now);

        header.Should().StartWith("vapid t=").And.EndWith($", k={publicKey}");
        var token = header["vapid t=".Length..header.IndexOf(',')];
        var parts = token.Split('.');
        var claims = System.Text.Json.JsonDocument.Parse(B(parts[1])).RootElement;
        claims.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
        claims.GetProperty("sub").GetString().Should().Be("https://piomanage.com");
        claims.GetProperty("exp").GetInt64().Should().Be(now.AddHours(12).ToUnixTimeSeconds());

        // Verifiable with the public key alone, which is all the push service has.
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = B(publicKey)[1..33], Y = B(publicKey)[33..65] },
        });
        verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B(parts[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue();
    }

    /// <summary>What the browser does with a message: the receiver's half of RFC 8291.</summary>
    private static byte[] Decrypt(byte[] body)
    {
        var salt = body[..16];
        var idLength = body[20];
        var asPublic = body[21..(21 + idLength)];
        var payload = body[(21 + idLength)..];

        using var receiver = Key(UaPublic, UaPrivate);
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] },
        });
        var ecdh = receiver.DeriveRawSecretAgreement(sender.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(B(UaPublic)).Concat(asPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, B(AuthSecret), keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plain = new byte[payload.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, payload[..^16], payload[^16..], plain);
        plain[^1].Should().Be(0x02, "the last record ends with the padding delimiter");
        return plain[..^1];
    }
}

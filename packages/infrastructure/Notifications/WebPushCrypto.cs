using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Desk.Infrastructure.Notifications;

/// <summary>
/// Web Push message encryption (RFC 8291, "aes128gcm" content coding from RFC 8188) and the VAPID
/// signature a push service asks for (RFC 8292), built on the platform's own cryptography rather than a
/// third-party package. Proven against the worked example in RFC 8291 Appendix A, byte for byte.
///
/// A push service only relays: it cannot read the message, which is encrypted to the browser's own key.
/// </summary>
public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> for one browser subscription and returns the request body.
    /// <paramref name="senderKey"/> and <paramref name="salt"/> are fresh for every message in real use;
    /// they are parameters only so the RFC's example can be reproduced exactly.
    /// </summary>
    public static byte[] Encrypt(byte[] uaPublic, byte[] authSecret, byte[] plaintext, ECDiffieHellman? senderKey = null, byte[]? salt = null)
    {
        using var ephemeral = senderKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var sender = senderKey ?? ephemeral!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04) throw new ArgumentException("The subscription key is not an uncompressed P-256 point.");
        if (authSecret.Length != 16) throw new ArgumentException("The subscription's auth secret must be 16 bytes.");

        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var asPublic = PublicKeyBytes(sender.ExportParameters(false));
        var ecdhSecret = sender.DeriveRawSecretAgreement(receiver.PublicKey);

        // RFC 8291 §3.4: combine the ECDH secret with the auth secret, bound to both public keys.
        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        // RFC 8188 §2.2 / 2.3: the content key and nonce, from the message's salt.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // One record, so it is the last: the padding delimiter is 0x02 and no padding follows.
        var padded = Concat(plaintext, [0x02]);
        if (padded.Length + 16 > RecordSize) throw new ArgumentException("The message is too long for one push record.");
        var ciphertext = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
            aes.Encrypt(nonce, padded, ciphertext, tag);

        // Header: salt (16) | record size (4, big-endian) | key id length (1) | key id = sender public key.
        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, ciphertext, tag);
    }

    /// <summary>
    /// The Authorization header value for one push service (RFC 8292): a short-lived ES256 token for the
    /// service's origin, and our public key. <paramref name="subject"/> is how the push service reaches
    /// the sender - the portal's address.
    /// </summary>
    public static string VapidAuthorization(string endpoint, ECDsa signingKey, string subject, DateTimeOffset now)
    {
        var origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = origin,
            // Twelve hours: the RFC's ceiling is a day, and nothing here reuses a token for long.
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");
        var signature = signingKey.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var publicKey = Base64Url(PublicKeyBytes(signingKey.ExportParameters(false)));
        return $"vapid t={header}.{claims}.{Base64Url(signature)}, k={publicKey}";
    }

    /// <summary>A new VAPID key pair: the public point (for browsers) and the private scalar (for the secret store).</summary>
    public static (string PublicKey, string PrivateKey) NewVapidKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (Base64Url(PublicKeyBytes(p)), Base64Url(p.D!));
    }

    /// <summary>Rebuilds the signing key from what <see cref="NewVapidKeys"/> produced.</summary>
    public static ECDsa VapidKey(string publicKey, string privateKey)
    {
        var point = FromBase64Url(publicKey);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = FromBase64Url(privateKey),
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
    }

    public static byte[] PublicKeyBytes(ECParameters p) => Concat([0x04], p.Q.X!, p.Q.Y!);

    public static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }
        return result;
    }
}

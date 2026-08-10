using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Secrets;

/// <summary>
/// Authenticated encryption for secrets at rest (the BYO-key API keys, #65). AES-256-GCM via the BCL
/// primitive; the master key comes from <c>CONDUX_SECRET_KEY</c> (base64, 32 bytes). A sealed blob is
/// <c>nonce(12) || tag(16) || ciphertext</c>, so it is self-describing and integrity-protected — a
/// tampered blob fails to open. Envelope encryption with an external KMS is a later swap behind the
/// same seal/open shape.
/// </summary>
public sealed class SecretBox
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    // The sealed blob is versioned so a later scheme (envelope / KMS-wrapped key) can coexist with v1
    // blobs — Open dispatches on the leading byte, so adopting KMS needs no forced re-encryption.
    private const byte V1DirectAesGcm = 1;

    private readonly byte[] _key;

    public SecretBox(string base64Key)
    {
        try
        {
            _key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            throw new ArgumentException("CONDUX_SECRET_KEY must be base64.", nameof(base64Key));
        }

        if (_key.Length != KeySize)
        {
            throw new ArgumentException(
                $"CONDUX_SECRET_KEY must decode to {KeySize} bytes (a 256-bit key).", nameof(base64Key));
        }
    }

    /// <summary>Encrypts UTF-8 <paramref name="plaintext"/> to a self-describing sealed blob.</summary>
    public byte[] Seal(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[1 + NonceSize + TagSize + cipher.Length];
        blob[0] = V1DirectAesGcm;
        nonce.CopyTo(blob, 1);
        tag.CopyTo(blob, 1 + NonceSize);
        cipher.CopyTo(blob, 1 + NonceSize + TagSize);
        return blob;
    }

    /// <summary>Decrypts a blob produced by <see cref="Seal"/>. Throws if it was tampered with or was
    /// sealed under a different key.</summary>
    public string Open(byte[] blob)
    {
        if (blob.Length < 1 + NonceSize + TagSize)
        {
            throw new CryptographicException("Sealed blob is too short.");
        }
        if (blob[0] != V1DirectAesGcm)
        {
            throw new CryptographicException($"Unsupported sealed-blob version {blob[0]}.");
        }

        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        var cipher = blob.AsSpan(1 + NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}

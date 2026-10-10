using System.Security.Cryptography;
using Yita.Translation;

namespace Yita.Core.Platform;

internal sealed class AesGcmTranslationMemoryProtector : ITranslationMemoryProtector, IDisposable
{
    private static readonly byte[] Header = "YITA-MEM\x01"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly object _gate = new();
    private readonly byte[] _key;
    private bool _disposed;

    internal AesGcmTranslationMemoryProtector(byte[] key)
    {
        if (key.Length != 32) throw new CryptographicException("Translation memory requires a 256-bit key.");
        _key = key.ToArray();
    }

    public byte[] Protect(byte[] plaintext)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var envelope = new byte[checked(Header.Length + NonceSize + TagSize + plaintext.Length)];
            Header.CopyTo(envelope, 0);
            var nonce = envelope.AsSpan(Header.Length, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintext, envelope.AsSpan(Header.Length + NonceSize + TagSize),
                envelope.AsSpan(Header.Length + NonceSize, TagSize), Header);
            return envelope;
        }
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var prefixLength = Header.Length + NonceSize + TagSize;
            if (protectedData.Length < prefixLength || !protectedData.AsSpan(0, Header.Length).SequenceEqual(Header))
                throw new CryptographicException("Translation memory has an unsupported encrypted format.");
            var plaintext = new byte[protectedData.Length - prefixLength];
            try
            {
                using var aes = new AesGcm(_key, TagSize);
                aes.Decrypt(protectedData.AsSpan(Header.Length, NonceSize), protectedData.AsSpan(prefixLength),
                    protectedData.AsSpan(Header.Length + NonceSize, TagSize), plaintext, Header);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}

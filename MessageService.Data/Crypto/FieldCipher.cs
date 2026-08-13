using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessageService.Data.Crypto;

/// <summary>整值 AES-256-GCM 加解密，套用在短文字欄位（訊息內文、群組/成員名稱、頭貼 URL、
/// 別名、檔名）。存成 "ENC1:" 前綴 + base64(nonce(12) + tag(16) + ciphertext)——讀取端看前綴
/// 判斷要不要解密，讓「加密啟用前寫入的舊資料」跟新資料混存，不需要一次性轉換作業。
/// blob（MessageContents.Content）不走這裡，Range 拖進度需要分塊加解密，見
/// ContentStreamService／DbContentWorkSource 對 ChunkedBlobCipher 的使用。</summary>
public class FieldCipher
{
    private const string Prefix = "ENC1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;
    private readonly ILogger<FieldCipher> _logger;

    public FieldCipher(IOptions<EncryptionOptions> options, ILogger<FieldCipher> logger)
    {
        _logger = logger;
        var opts = options.Value;
        if (!opts.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Key))
        {
            throw new InvalidOperationException("Encryption:Key must be set when Encryption:Enabled=true.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(opts.Key);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Encryption:Key must be valid base64.", ex);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Encryption:Key must decode to 32 bytes (AES-256), got {key.Length} bytes.");
        }

        _key = key;
        KeyId = ComputeKeyId(key);
    }

    /// <summary>金鑰指紋：SHA-256 前 4 bytes 轉小寫 hex（8 個字元），從金鑰本身導出、不是另外的
    /// 設定值。不會洩漏金鑰本身（單向雜湊），用途：(1) 多台直連資料庫的主機互相比對，金鑰
    /// 設定不一致時能立刻看出來，不用等到畫面上出現解不開的 ENC1: 密文才發現；
    /// (2) 未來加密信封帶 key id 時（見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E），
    /// 沿用同一個值，不用兩套指紋定義。未啟用加密時為 null。</summary>
    public string? KeyId { get; }

    private static string ComputeKeyId(byte[] key) =>
        Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 4));

    /// <summary>測試／未啟用加密情境用：一律不加密，Encrypt 原樣傳回、Decrypt 只在偵測到
    /// ENC1: 前綴時才嘗試解密（沒有金鑰的話會失敗並原樣回傳，見 Decrypt 說明）。</summary>
    public static FieldCipher Disabled { get; } =
        new(Options.Create(new EncryptionOptions()), NullLogger<FieldCipher>.Instance);

    public bool Enabled => _key is not null;

    public string Encrypt(string plaintext)
    {
        if (_key is null)
        {
            return plaintext;
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var combined = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize + TagSize, ciphertext.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    /// <summary>沒有 ENC1: 前綴＝加密啟用前寫入的舊資料，原樣傳回。有前綴但解不開（沒有金鑰、
    /// 金鑰不對、或內容損毀／有人惡意貼了長得像密文的訊息）一律原樣傳回並記 log，不拋例外——
    /// 訊息串不該因為一筆解不開的欄位整個 500。</summary>
    public string Decrypt(string storedValue)
    {
        if (!storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return storedValue;
        }

        if (_key is null)
        {
            return storedValue;
        }

        try
        {
            var combined = Convert.FromBase64String(storedValue[Prefix.Length..]);
            if (combined.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("Encrypted payload too short.");
            }

            var nonce = combined.AsSpan(0, NonceSize);
            var tag = combined.AsSpan(NonceSize, TagSize);
            var ciphertext = combined.AsSpan(NonceSize + TagSize);
            var plaintextBytes = new byte[ciphertext.Length];

            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to decrypt field value; returning raw stored value");
            return storedValue;
        }
    }

    /// <summary>沒啟用加密時原樣傳回來源串流（不包一層）——DbContentWorkSource 不需要另外判斷
    /// 要不要包，直接把回傳值交給既有的串流寫入路徑即可。</summary>
    public Stream CreateEncryptingStream(Stream source, long plaintextLength) =>
        _key is null ? source : new ChunkedEncryptingStream(source, plaintextLength, _key);

    /// <summary>解密 ContentStreamService 從密文 blob 讀出的其中一個 chunk（nonce+tag+ciphertext）。
    /// 沒有金鑰時沒辦法解，呼叫端要自行處理（見 ContentStreamService：視同內容不可用，回 404）。</summary>
    public byte[] DecryptChunk(ReadOnlySpan<byte> encryptedChunk) =>
        _key is null
            ? throw new InvalidOperationException("Cannot decrypt blob chunk: no encryption key configured.")
            : ChunkedBlobCipher.DecryptChunk(encryptedChunk, _key);
}

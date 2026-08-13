using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MessageService.Data.Crypto;

/// <summary>整值 AES-256-GCM 加解密，套用在短文字欄位（訊息內文、群組/成員名稱、頭貼 URL、
/// 別名、檔名）。新寫入存成 "ENC2:&lt;keyId&gt;:" 前綴 + base64(nonce(12) + tag(16) + ciphertext)——
/// keyId 是 8 碼小寫 hex（見 KeyId 說明），讀取端拿它跟目前設定的金鑰指紋比對，指紋不一致
/// 時直接知道「這是拿哪把金鑰加密的」而不用先試著解密才發現失敗，也是未來真的要輪替金鑰時
/// （見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E）唯一能分辨舊資料是哪把金鑰加的依據。
/// 讀取端同時認舊格式 "ENC1:"（沒有 key id，直接嘗試用目前金鑰解，解不開就原樣傳回）——
/// 讓「加密啟用前寫入的舊資料」與「ENC1 時期寫入的資料」都跟新資料混存，不需要一次性轉換
/// 作業。blob（MessageContents.Content）不走這裡，Range 拖進度需要分塊加解密，格式見
/// ChunkedBlobCipher，本類別透過 CreateEncryptingStream／DecryptChunk 提供金鑰。</summary>
public class FieldCipher
{
    private const string PrefixV1 = "ENC1:";
    private const string PrefixV2 = "ENC2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[]? _key;
    private readonly byte _keyIdByte;
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
        var keyIdBytes = SHA256.HashData(key).AsSpan(0, 4);
        KeyId = Convert.ToHexStringLower(keyIdBytes);
        _keyIdByte = keyIdBytes[0];
    }

    /// <summary>金鑰指紋：SHA-256 前 4 bytes 轉小寫 hex（8 個字元），從金鑰本身導出、不是另外的
    /// 設定值。不會洩漏金鑰本身（單向雜湊），用途：(1) 多台直連資料庫的主機互相比對，金鑰
    /// 設定不一致時能立刻看出來，見 HeartbeatReport／HostHeartbeat；(2) 文字欄位的 ENC2: 信封
    /// 與 blob 的 MSE2 表頭都帶著同一份指紋（blob 表頭空間只夠留第一個 byte，見
    /// ChunkedBlobCipher.BuildHeader 的 MSE2 多載），解密前就能分辨這筆資料是哪把金鑰加的。
    /// 未啟用加密時為 null。</summary>
    public string? KeyId { get; }

    /// <summary>測試／未啟用加密情境用：一律不加密，Encrypt 原樣傳回、Decrypt 只在偵測到
    /// ENC1:／ENC2: 前綴時才嘗試解密（沒有金鑰的話會失敗並原樣回傳，見 Decrypt 說明）。</summary>
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

        return $"{PrefixV2}{KeyId}:{Convert.ToBase64String(combined)}";
    }

    /// <summary>沒有 ENC1:／ENC2: 前綴＝加密啟用前寫入的舊資料，原樣傳回。解不開（沒有金鑰、
    /// keyId 跟目前設定的金鑰指紋不符、內容損毀，或有人惡意貼了長得像密文的訊息）一律原樣
    /// 傳回並記 log，不拋例外——訊息串不該因為一筆解不開的欄位整個 500。</summary>
    public string Decrypt(string storedValue)
    {
        if (storedValue.StartsWith(PrefixV2, StringComparison.Ordinal))
        {
            return DecryptV2(storedValue);
        }

        if (storedValue.StartsWith(PrefixV1, StringComparison.Ordinal))
        {
            return DecryptV1(storedValue);
        }

        return storedValue;
    }

    private string DecryptV2(string storedValue)
    {
        if (_key is null)
        {
            return storedValue;
        }

        var rest = storedValue.AsSpan(PrefixV2.Length);
        var separatorIndex = rest.IndexOf(':');
        if (separatorIndex < 0)
        {
            _logger.LogWarning("Encrypted field value has an ENC2: prefix but is missing the key id separator; returning raw stored value");
            return storedValue;
        }

        var storedKeyId = rest[..separatorIndex];
        // keyId 不符代表這筆是用另一把金鑰加的（金鑰輪替，或多台主機的 Encryption:Key 設定
        // 沒對齊）——直接短路回原樣，比讓 AES-GCM 的認證標籤驗證失敗才發現快，log 也講得
        // 更清楚是「金鑰不一致」而不是單純的「解密失敗」
        if (!storedKeyId.Equals(KeyId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Encrypted field value was written with a different key (stored keyId {StoredKeyId}, configured keyId {ConfiguredKeyId}); returning raw stored value",
                storedKeyId.ToString(), KeyId);
            return storedValue;
        }

        return DecryptPayload(rest[(separatorIndex + 1)..].ToString(), storedValue, _key);
    }

    private string DecryptV1(string storedValue)
    {
        if (_key is null)
        {
            return storedValue;
        }

        return DecryptPayload(storedValue[PrefixV1.Length..], storedValue, _key);
    }

    private string DecryptPayload(string payloadBase64, string rawStoredValueForFallback, byte[] key)
    {
        try
        {
            var combined = Convert.FromBase64String(payloadBase64);
            if (combined.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("Encrypted payload too short.");
            }

            var nonce = combined.AsSpan(0, NonceSize);
            var tag = combined.AsSpan(NonceSize, TagSize);
            var ciphertext = combined.AsSpan(NonceSize + TagSize);
            var plaintextBytes = new byte[ciphertext.Length];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            }

            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            _logger.LogWarning(ex, "Failed to decrypt field value; returning raw stored value");
            return rawStoredValueForFallback;
        }
    }

    /// <summary>沒啟用加密時原樣傳回來源串流（不包一層）——DbContentWorkSource 不需要另外判斷
    /// 要不要包，直接把回傳值交給既有的串流寫入路徑即可。keyId 傳給 ChunkedEncryptingStream
    /// 寫進 MSE2 表頭，見 ChunkedBlobCipher.BuildHeader 的說明。</summary>
    public Stream CreateEncryptingStream(Stream source, long plaintextLength) =>
        _key is null ? source : new ChunkedEncryptingStream(source, plaintextLength, _key, _keyIdByte);

    /// <summary>blob 表頭的 key id（MSE2 才有，MSE1 回傳 null）跟目前設定的金鑰指紋不符時，
    /// 直接判定為「這顆 blob 是用另一把金鑰加的」——不用等 DecryptChunk 的 AES-GCM 認證標籤
    /// 驗證失敗才發現（而且那時 response 可能已經開始寫入，來不及乾淨地回 404，見
    /// ContentStreamService 的說明）。MSE1（headerKeyId 為 null）沒有指紋可比，一律放行，
    /// 交給既有的「解不開就當內容不可用」邏輯處理。</summary>
    public bool MatchesKeyId(byte? headerKeyId) => headerKeyId is null || headerKeyId == _keyIdByte;

    /// <summary>解密 ContentStreamService 從密文 blob 讀出的其中一個 chunk（nonce+tag+ciphertext）。
    /// 沒有金鑰時沒辦法解，呼叫端要自行處理（見 ContentStreamService：視同內容不可用，回 404）。</summary>
    public byte[] DecryptChunk(ReadOnlySpan<byte> encryptedChunk) =>
        _key is null
            ? throw new InvalidOperationException("Cannot decrypt blob chunk: no encryption key configured.")
            : ChunkedBlobCipher.DecryptChunk(encryptedChunk, _key);
}

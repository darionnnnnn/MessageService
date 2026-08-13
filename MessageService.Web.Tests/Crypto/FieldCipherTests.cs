using MessageService.Data.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Crypto;

public class FieldCipherTests
{
    private const string ValidBase64Key32Bytes = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private static FieldCipher CreateEnabled(string key = ValidBase64Key32Bytes) =>
        new(OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = key }), NullLogger<FieldCipher>.Instance);

    [Fact]
    public void Encrypt_ThenDecrypt_RoundTripsExactly()
    {
        var cipher = CreateEnabled();

        var encrypted = cipher.Encrypt("我的密碼是1234，記得騎腳踏車");
        var decrypted = cipher.Decrypt(encrypted);

        Assert.Equal("我的密碼是1234，記得騎腳踏車", decrypted);
    }

    [Fact]
    public void Encrypt_ProducesEnc2PrefixedValueWithKeyId()
    {
        var cipher = CreateEnabled();

        var encrypted = cipher.Encrypt("hello");

        Assert.StartsWith($"ENC2:{cipher.KeyId}:", encrypted);
    }

    // === ENC2 keyId：見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次E ===

    [Fact]
    public void Decrypt_Enc2WithMismatchedKeyId_ReturnsRawValueInsteadOfThrowing()
    {
        // 跟 Decrypt_WrongKey 情境不同：這裡刻意驗證的是「keyId 比對」這條短路路徑本身
        // （不需要真的解密失敗才發現），跟舊格式 ENC1 全靠 AES-GCM 認證標籤失敗來判斷不一樣
        var writer = CreateEnabled(ValidBase64Key32Bytes);
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        var reader = CreateEnabled(otherKey);

        var encrypted = writer.Encrypt("secret");
        Assert.NotEqual(writer.KeyId, reader.KeyId); // 前提：兩把金鑰的指紋確實不同

        var result = reader.Decrypt(encrypted);

        Assert.Equal(encrypted, result);
    }

    [Fact]
    public void Decrypt_Enc2MissingKeyIdSeparator_ReturnsRawValueInsteadOfThrowing()
    {
        var cipher = CreateEnabled();

        var result = cipher.Decrypt("ENC2:not-well-formed-no-second-colon");

        Assert.Equal("ENC2:not-well-formed-no-second-colon", result);
    }

    [Fact]
    public void Decrypt_Enc2MalformedBase64Payload_ReturnsRawValueInsteadOfThrowing()
    {
        var cipher = CreateEnabled();

        var result = cipher.Decrypt($"ENC2:{cipher.KeyId}:not-valid-base64!!!");

        Assert.Equal($"ENC2:{cipher.KeyId}:not-valid-base64!!!", result);
    }

    [Fact]
    public void Decrypt_LegacyEnc1Value_StillDecryptsWithCurrentKey()
    {
        // ENC1（沒有 keyId 的舊格式）要一直讀得到，不需要一次性轉換作業——見類別說明。
        // FieldCipher 本身不再對外暴露「產生 ENC1 格式」的方法（新寫入一律是 ENC2），
        // 這裡用同一把金鑰手動組一份 ENC1 payload，模擬「加密啟用前一輪用舊版程式碼寫入、
        // 現在才第一次被新版程式碼讀到」的既有資料
        var cipher = CreateEnabled();
        var plaintext = "舊格式訊息";
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        var key = Convert.FromBase64String(ValidBase64Key32Bytes);
        using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }
        var combined = new byte[12 + 16 + ciphertext.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, 12);
        ciphertext.CopyTo(combined, 28);
        var legacyEncrypted = "ENC1:" + Convert.ToBase64String(combined);

        var result = cipher.Decrypt(legacyEncrypted);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentCiphertext()
    {
        // nonce 每次隨機——同樣的明文加密兩次密文應該不同，避免相同訊息內容洩漏出「這兩則一樣」
        var cipher = CreateEnabled();

        var first = cipher.Encrypt("hello");
        var second = cipher.Encrypt("hello");

        Assert.NotEqual(first, second);
        Assert.Equal("hello", cipher.Decrypt(first));
        Assert.Equal("hello", cipher.Decrypt(second));
    }

    [Fact]
    public void Decrypt_PlainValueWithoutPrefix_ReturnsAsIs()
    {
        // 加密啟用前寫入的舊資料：沒有 ENC1: 前綴，原樣傳回
        var cipher = CreateEnabled();

        var result = cipher.Decrypt("這是加密啟用前的舊訊息");

        Assert.Equal("這是加密啟用前的舊訊息", result);
    }

    [Fact]
    public void Decrypt_WrongKey_ReturnsRawValueInsteadOfThrowing()
    {
        var writer = CreateEnabled(ValidBase64Key32Bytes);
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 32).ToArray());
        var reader = CreateEnabled(otherKey);

        var encrypted = writer.Encrypt("secret");
        var result = reader.Decrypt(encrypted);

        Assert.Equal(encrypted, result); // 解不開就原樣傳回，不拋例外
    }

    [Fact]
    public void Decrypt_CorruptedPayload_ReturnsRawValueInsteadOfThrowing()
    {
        var cipher = CreateEnabled();

        var result = cipher.Decrypt("ENC1:not-valid-base64!!!");

        Assert.Equal("ENC1:not-valid-base64!!!", result);
    }

    [Fact]
    public void Decrypt_MaliciousUserTypedEnc1Prefix_DoesNotThrow()
    {
        // 有人在聊天室裡真的打了 "ENC1:xxx" 這種字串當訊息內容——不該讓應用程式炸掉
        var cipher = CreateEnabled();

        var result = cipher.Decrypt("ENC1:哈囉大家好");

        Assert.Equal("ENC1:哈囉大家好", result);
    }

    [Fact]
    public void Disabled_EncryptReturnsPlaintextUnchanged()
    {
        var cipher = FieldCipher.Disabled;

        Assert.Equal("hello", cipher.Encrypt("hello"));
        Assert.False(cipher.Enabled);
    }

    [Fact]
    public void Disabled_DecryptEnc1Value_ReturnsRawValue_NoKeyToDecryptWith()
    {
        var writer = CreateEnabled();
        var encrypted = writer.Encrypt("secret");

        var result = FieldCipher.Disabled.Decrypt(encrypted);

        Assert.Equal(encrypted, result);
    }

    [Fact]
    public void Constructor_EnabledWithoutKey_Throws()
    {
        var ex = Record.Exception(() =>
            new FieldCipher(OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = null }), NullLogger<FieldCipher>.Instance));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Constructor_EnabledWithInvalidBase64Key_Throws()
    {
        var ex = Record.Exception(() =>
            new FieldCipher(OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = "not-base64!!!" }), NullLogger<FieldCipher>.Instance));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Constructor_EnabledWithWrongKeyLength_Throws()
    {
        var shortKey = Convert.ToBase64String(new byte[16]); // AES-128 長度，不是要求的 32 bytes
        var ex = Record.Exception(() =>
            new FieldCipher(OptionsFactory.Create(new EncryptionOptions { Enabled = true, Key = shortKey }), NullLogger<FieldCipher>.Instance));

        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Constructor_DisabledWithNoKey_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            new FieldCipher(OptionsFactory.Create(new EncryptionOptions { Enabled = false }), NullLogger<FieldCipher>.Instance));

        Assert.Null(ex);
    }

    // === KeyId：金鑰指紋，供心跳互相比對用（見 docs/POST-CONSOLIDATION-REVIEW-PLAN.md 批次D／E）===

    [Fact]
    public void KeyId_Disabled_IsNull()
    {
        Assert.Null(FieldCipher.Disabled.KeyId);
    }

    [Fact]
    public void KeyId_Enabled_Is8LowercaseHexChars()
    {
        var cipher = CreateEnabled();

        Assert.NotNull(cipher.KeyId);
        Assert.Equal(8, cipher.KeyId!.Length);
        Assert.Matches("^[0-9a-f]{8}$", cipher.KeyId);
    }

    [Fact]
    public void KeyId_SameKeyTwice_ProducesSameFingerprint()
    {
        var first = CreateEnabled();
        var second = CreateEnabled();

        Assert.Equal(first.KeyId, second.KeyId);
    }

    [Fact]
    public void KeyId_DifferentKeys_ProduceDifferentFingerprints()
    {
        var first = CreateEnabled();
        var second = CreateEnabled(Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray()));

        Assert.NotEqual(first.KeyId, second.KeyId);
    }

    // === MatchesKeyId：blob 表頭（ChunkedBlobCipher.ReadKeyId）跟目前設定的金鑰指紋比對，
    // 見 ContentStreamService 的說明 ===

    [Fact]
    public void MatchesKeyId_NullHeaderKeyId_ReturnsTrue()
    {
        // MSE1（舊格式）沒有 key id 可比——一律放行，交給既有的「解不開就當內容不可用」邏輯
        var cipher = CreateEnabled();

        Assert.True(cipher.MatchesKeyId(null));
    }

    [Fact]
    public void MatchesKeyId_MatchingFirstByteOfKeyId_ReturnsTrue()
    {
        var cipher = CreateEnabled();
        var firstByte = Convert.FromHexString(cipher.KeyId!)[0];

        Assert.True(cipher.MatchesKeyId(firstByte));
    }

    [Fact]
    public void MatchesKeyId_MismatchedByte_ReturnsFalse()
    {
        var cipher = CreateEnabled();
        var firstByte = Convert.FromHexString(cipher.KeyId!)[0];
        var wrongByte = (byte)(firstByte + 1);

        Assert.False(cipher.MatchesKeyId(wrongByte));
    }

    [Fact]
    public void Encrypt_EmptyString_RoundTrips()
    {
        var cipher = CreateEnabled();

        var encrypted = cipher.Encrypt("");

        Assert.Equal("", cipher.Decrypt(encrypted));
    }
}

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
    public void Encrypt_ProducesEnc1PrefixedValue()
    {
        var cipher = CreateEnabled();

        var encrypted = cipher.Encrypt("hello");

        Assert.StartsWith("ENC1:", encrypted);
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

    [Fact]
    public void Encrypt_EmptyString_RoundTrips()
    {
        var cipher = CreateEnabled();

        var encrypted = cipher.Encrypt("");

        Assert.Equal("", cipher.Decrypt(encrypted));
    }
}

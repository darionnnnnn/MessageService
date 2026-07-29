using System.Security.Cryptography;
using System.Text;
using MessageService.Options;
using MessageService.Services;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace MessageService.Tests.Services;

public class LineSignatureValidatorTests
{
    private const string Secret = "test-channel-secret";

    private static LineSignatureValidator CreateValidator(string secret = Secret) =>
        new(OptionsFactory.Create(new LineOptions { ChannelSecret = secret }));

    private static string ComputeSignature(string secret, byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Convert.ToBase64String(hash);
    }

    [Fact]
    public void IsValid_ReturnsTrue_ForCorrectSignature()
    {
        var validator = CreateValidator();
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var signature = ComputeSignature(Secret, body);

        Assert.True(validator.IsValid(body, signature));
    }

    [Fact]
    public void IsValid_ReturnsFalse_ForIncorrectSignature()
    {
        var validator = CreateValidator();
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");

        Assert.False(validator.IsValid(body, "not-the-right-signature"));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenSignatureHeaderMissing()
    {
        var validator = CreateValidator();
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");

        Assert.False(validator.IsValid(body, null));
    }

    [Fact]
    public void IsValid_ReturnsFalse_WhenChannelSecretNotConfigured()
    {
        var validator = CreateValidator(secret: "");
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");

        Assert.False(validator.IsValid(body, "anything"));
    }
}

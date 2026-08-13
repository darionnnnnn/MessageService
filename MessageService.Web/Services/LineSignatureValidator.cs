using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MessageService.Options;

namespace MessageService.Services;

public class LineSignatureValidator(IOptions<LineOptions> options) : ILineSignatureValidator
{
    private readonly LineOptions _options = options.Value;

    public bool IsValid(byte[] requestBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(_options.ChannelSecret))
        {
            return false;
        }

        var key = System.Text.Encoding.UTF8.GetBytes(_options.ChannelSecret);
        var hash = HMACSHA256.HashData(key, requestBody);
        var computedSignature = Convert.ToBase64String(hash);

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computedSignature),
            System.Text.Encoding.UTF8.GetBytes(signatureHeader));
    }
}

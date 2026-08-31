using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MessageService.Options;

namespace MessageService.Services;

public class LineSignatureValidator(IOptionsMonitor<LineOptions> options) : ILineSignatureValidator
{
    public bool IsValid(byte[] requestBody, string? signatureHeader)
    {
        var secret = options.CurrentValue.ChannelSecret;
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var key = System.Text.Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, requestBody);
        var computedSignature = Convert.ToBase64String(hash);

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(computedSignature),
            System.Text.Encoding.UTF8.GetBytes(signatureHeader));
    }
}

namespace MessageService.Services;

public interface ILineSignatureValidator
{
    bool IsValid(byte[] requestBody, string? signatureHeader);
}

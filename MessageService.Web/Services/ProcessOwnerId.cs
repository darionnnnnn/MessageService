using System.Security.Cryptography;
using System.Text;

namespace MessageService.Services;

/// <summary>代表目前站台的下載端身分識別碼（MachineName + 站台鍵 SHA-256 十六進位前 8 碼）。
/// 同一主機上的同一個站台固定不變，作為認領租約的 ownerId。</summary>
public sealed class ProcessOwnerId
{
    public static ProcessOwnerId Instance { get; } = new();

    public string Value { get; }

    public ProcessOwnerId(string? siteKey = null)
    {
        var machine = Environment.MachineName;
        var key = siteKey ?? AppContext.BaseDirectory;
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        var combined = $"{machine}-{suffix}";
        Value = combined.Length <= 128 ? combined : combined[..128];
    }

    public override string ToString() => Value;
}

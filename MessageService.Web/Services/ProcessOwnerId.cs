namespace MessageService.Services;

/// <summary>代表目前行程的下載端身分識別碼（MachineName + 行程啟動時產生的隨機短字尾）。
/// 每個行程一個、行程存活期間固定不變，作為認領租約的 ownerId。</summary>
public sealed class ProcessOwnerId
{
    public static ProcessOwnerId Instance { get; } = new();

    public string Value { get; }

    public ProcessOwnerId()
    {
        var machine = Environment.MachineName;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var combined = $"{machine}-{suffix}";
        Value = combined.Length <= 128 ? combined : combined[..128];
    }

    public override string ToString() => Value;
}

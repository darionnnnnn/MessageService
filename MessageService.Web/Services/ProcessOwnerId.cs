using System.Security.Cryptography;
using System.Text;

namespace MessageService.Services;

/// <summary>代表目前站台的下載端身分識別碼（MachineName + 站台鍵 SHA-256 十六進位前 8 碼）。
/// 同一主機上的同一個站台跨行程重啟固定不變（這正是啟動掃描能認出自己上次崩潰留下的孤兒的前提），
/// 作為認領租約的 ownerId。類別名沿用歷史名稱，語意已經是站台而不是行程。
///
/// 兩個部署前提（違反的話認領語意會壞掉，見 docs/DEPLOYMENT-MODES.md）：
/// 一、**不可啟用 ASP.NET Core Module 的 shadow copy**——BaseDirectory 每次啟動都是新的暫存目錄，
/// ownerId 會退回「每行程一個」，啟動掃描認不出自己的孤兒。
/// 二、**同一台機器上的兩個站台不可共用同一個實體目錄**——那會讓它們拿到相同的 ownerId。</summary>
public sealed class ProcessOwnerId
{
    public static ProcessOwnerId Instance { get; } = new();

    public string Value { get; }

    public ProcessOwnerId(string? siteKey = null)
    {
        var key = siteKey ?? AppContext.BaseDirectory;
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];

        // 欄位上限 128。要截也是截機器名而不是截尾巴的雜湊——雜湊被截掉的話，同一台機器上
        // 的所有站台會拿到同一個 ownerId，啟動掃描就會互搶對方的認領（Linux／容器主機名沒有
        // Windows 的 15 字元限制，這條不是純理論）
        var machine = Environment.MachineName;
        var maxMachineLength = 128 - suffix.Length - 1;
        if (machine.Length > maxMachineLength)
        {
            machine = machine[..maxMachineLength];
        }

        Value = $"{machine}-{suffix}";
    }

    public override string ToString() => Value;
}

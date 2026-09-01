using MessageService.Options;
using Microsoft.Extensions.Configuration;

namespace MessageService.Web.Configuration;

/// <summary>
/// 部署模式設定檔定位結果狀態。
/// </summary>
public enum DeploymentModeFileLocatorStatus
{
    /// <summary>未找到任何後綴設定檔。</summary>
    None,

    /// <summary>找到恰好一份後綴設定檔。</summary>
    Found,

    /// <summary>找到兩份以上後綴設定檔（衝突）。</summary>
    Conflict
}

/// <summary>
/// 部署模式設定檔定位結果。
/// </summary>
public sealed class DeploymentModeFileLocatorResult
{
    public DeploymentModeFileLocatorStatus Status { get; }
    public string? FilePath { get; }
    public string? FileName => FilePath is not null ? Path.GetFileName(FilePath) : null;
    public DeploymentMode? Mode { get; }
    public IReadOnlyList<string> ConflictingFiles { get; }

    /// <summary>檔名長得像後綴檔、但模式段解析不出來的檔案（拼錯或用了舊名）。
    /// 這些檔案不影響判別結果，但要讓呼叫端有機會提醒——否則「放了設定檔卻起成 AllInOne」無從查起。</summary>
    public IReadOnlyList<string> IgnoredFiles { get; }

    private DeploymentModeFileLocatorResult(
        DeploymentModeFileLocatorStatus status,
        string? filePath,
        DeploymentMode? mode,
        IReadOnlyList<string> conflictingFiles,
        IReadOnlyList<string> ignoredFiles)
    {
        Status = status;
        FilePath = filePath;
        Mode = mode;
        ConflictingFiles = conflictingFiles;
        IgnoredFiles = ignoredFiles;
    }

    public static DeploymentModeFileLocatorResult None(IReadOnlyList<string>? ignoredFiles = null) =>
        new(DeploymentModeFileLocatorStatus.None, null, null, [], ignoredFiles ?? []);

    public static DeploymentModeFileLocatorResult Found(string filePath, DeploymentMode mode) =>
        new(DeploymentModeFileLocatorStatus.Found, filePath, mode, [], []);

    public static DeploymentModeFileLocatorResult Conflict(IReadOnlyList<string> conflictingFiles) =>
        new(DeploymentModeFileLocatorStatus.Conflict, null, null, conflictingFiles, []);
}

/// <summary>
/// 依檔名後綴（appsettings.{環境名}.{模式}.json）掃描並解析部署模式設定檔。
/// </summary>
public static class DeploymentModeFileLocator
{
    private static readonly HashSet<string> LegacyModeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Full",
        "Line",
        "Db"
    };

    /// <summary>
    /// 在指定目錄與環境名稱下掃描後綴設定檔。
    /// </summary>
    public static DeploymentModeFileLocatorResult Locate(string directoryPath, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) ||
            string.IsNullOrWhiteSpace(environmentName) ||
            !Directory.Exists(directoryPath))
        {
            return DeploymentModeFileLocatorResult.None();
        }

        var prefix = $"appsettings.{environmentName}.";
        const string suffix = ".json";

        var files = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly);
        var matches = new List<(string FilePath, string FileName, DeploymentMode Mode)>();
        var ignored = new List<string>();

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Length <= prefix.Length + suffix.Length)
            {
                continue;
            }

            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var modeSegment = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
            if (TryParseSuffixMode(modeSegment, out var mode))
            {
                matches.Add((file, fileName, mode));
            }
            else
            {
                ignored.Add(fileName);
            }
        }

        if (matches.Count == 0)
        {
            return DeploymentModeFileLocatorResult.None(ignored);
        }

        if (matches.Count == 1)
        {
            return DeploymentModeFileLocatorResult.Found(matches[0].FilePath, matches[0].Mode);
        }

        var conflictingNames = matches
            .Select(m => m.FileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return DeploymentModeFileLocatorResult.Conflict(conflictingNames);
    }

    /// <summary>
    /// 解析後綴模式名稱。僅接受五種新名稱（AllInOne、Edge、Core、Viewer、EdgeProxy），排除舊名稱與未知字串。
    /// </summary>
    public static bool TryParseSuffixMode(string? candidate, out DeploymentMode mode)
    {
        if (!string.IsNullOrWhiteSpace(candidate) &&
            !int.TryParse(candidate, out _) &&
            !candidate.Contains(',') &&
            !LegacyModeNames.Contains(candidate) &&
            Enum.TryParse<DeploymentMode>(candidate, ignoreCase: true, out mode) &&
            Enum.IsDefined(typeof(DeploymentMode), mode))
        {
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>
    /// 只讀後綴設定檔本身宣告的 Deployment:Mode，不看設定鏈上的其他來源。
    /// 一致性檢查必須以這個值為準：基底 appsettings.json 本來就帶 Mode，
    /// 拿整條鏈的值去比會讓「後綴檔不寫模式鍵」（本機制的正常用法）誤判成不一致。
    /// </summary>
    public static string? ReadDeclaredMode(string filePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(filePath, optional: false, reloadOnChange: false)
            .Build();

        return configuration["Deployment:Mode"];
    }

    /// <summary>
    /// 驗證後綴模式與設定檔中 Deployment:Mode 鍵值的一致性。
    /// </summary>
    public static void ValidateModeConsistency(DeploymentMode suffixMode, string suffixFileName, string? configuredModeValue)
    {
        if (string.IsNullOrWhiteSpace(configuredModeValue))
        {
            return;
        }

        if (!Enum.TryParse<DeploymentMode>(configuredModeValue.Trim(), ignoreCase: true, out var configuredMode) ||
            !Enum.IsDefined(typeof(DeploymentMode), configuredMode) ||
            configuredMode != suffixMode)
        {
            throw new InvalidOperationException(
                $"後綴設定檔 \"{suffixFileName}\" 所指定的模式為 {suffixMode}，但 Deployment:Mode 設定值為 \"{configuredModeValue}\"，兩者不一致。");
        }
    }
}

using MessageService.Options;
using MessageService.Web.Configuration;
using Xunit;

namespace MessageService.Web.Tests.Configuration;

public class DeploymentModeFileLocatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"messageservice-mode-test-{Guid.NewGuid():N}");

    public DeploymentModeFileLocatorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("appsettings.Production.Edge.json", DeploymentMode.Edge)]
    [InlineData("appsettings.Production.EdgeProxy.json", DeploymentMode.EdgeProxy)]
    [InlineData("appsettings.Production.Core.json", DeploymentMode.Core)]
    [InlineData("appsettings.Production.Viewer.json", DeploymentMode.Viewer)]
    [InlineData("appsettings.Production.AllInOne.json", DeploymentMode.AllInOne)]
    public void Locate_WhenSingleSuffixFileExists_ReturnsFoundWithCorrectMode(string fileName, DeploymentMode expectedMode)
    {
        var filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.Found, result.Status);
        Assert.Equal(expectedMode, result.Mode);
        Assert.Equal(filePath, result.FilePath);
        Assert.Equal(fileName, result.FileName);
        Assert.Empty(result.ConflictingFiles);
    }

    [Fact]
    public void Locate_WhenFilenameHasDifferentCasing_ReturnsFoundWithCorrectMode()
    {
        var fileName = "appsettings.production.edge.JSON";
        var filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.Found, result.Status);
        Assert.Equal(DeploymentMode.Edge, result.Mode);
        Assert.Equal(filePath, result.FilePath);
        Assert.Equal(fileName, result.FileName);
    }

    [Fact]
    public void Locate_WhenNoSuffixFilesExist_ReturnsNone()
    {
        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
        Assert.Null(result.FilePath);
        Assert.Null(result.FileName);
        Assert.Null(result.Mode);
        Assert.Empty(result.ConflictingFiles);
    }

    [Fact]
    public void Locate_WhenMultipleSuffixFilesExist_ReturnsConflictWithAllFilenames()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Edge.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Core.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.Conflict, result.Status);
        Assert.Null(result.FilePath);
        Assert.Null(result.FileName);
        Assert.Null(result.Mode);
        Assert.Equal(2, result.ConflictingFiles.Count);
        Assert.Contains("appsettings.Production.Core.json", result.ConflictingFiles);
        Assert.Contains("appsettings.Production.Edge.json", result.ConflictingFiles);
    }

    [Fact]
    public void Locate_WhenOnlyBaseEnvironmentFileExists_DoesNotTreatAsSuffixFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }

    [Fact]
    public void Locate_WhenUnknownOrLegacySuffixFilesExist_SilentlyIgnoresAndReturnsNone()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Foo.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Line.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Full.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Db.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }

    [Fact]
    public void Locate_WhenDifferentEnvironmentName_IgnoresOtherEnvironmentFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Edge.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Development");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }

    [Fact]
    public void Locate_WhenDirectoryDoesNotExist_ReturnsNone()
    {
        var nonExistentDir = Path.Combine(_tempDir, "non-existent-sub-dir");

        var result = DeploymentModeFileLocator.Locate(nonExistentDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_WhenEnvironmentNameIsNullOrWhitespace_ReturnsNone(string? environmentName)
    {
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Edge.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, environmentName!);

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }

    [Theory]
    [InlineData(DeploymentMode.Edge, "appsettings.Production.Edge.json", "Edge")]
    [InlineData(DeploymentMode.Edge, "appsettings.Production.Edge.json", "edge")]
    [InlineData(DeploymentMode.EdgeProxy, "appsettings.Production.EdgeProxy.json", "EdgeProxy")]
    [InlineData(DeploymentMode.Core, "appsettings.Production.Core.json", "Core")]
    [InlineData(DeploymentMode.Viewer, "appsettings.Production.Viewer.json", "Viewer")]
    [InlineData(DeploymentMode.AllInOne, "appsettings.Production.AllInOne.json", "AllInOne")]
    public void ValidateModeConsistency_WhenConfiguredModeMatches_DoesNotThrow(
        DeploymentMode suffixMode,
        string fileName,
        string configuredMode)
    {
        var exception = Record.Exception(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(suffixMode, fileName, configuredMode));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(DeploymentMode.Edge, "appsettings.Production.Edge.json", "Line")]
    [InlineData(DeploymentMode.Edge, "appsettings.Production.Edge.json", "line")]
    [InlineData(DeploymentMode.AllInOne, "appsettings.Production.AllInOne.json", "Full")]
    [InlineData(DeploymentMode.AllInOne, "appsettings.Production.AllInOne.json", "full")]
    [InlineData(DeploymentMode.Core, "appsettings.Production.Core.json", "Db")]
    [InlineData(DeploymentMode.Core, "appsettings.Production.Core.json", "db")]
    public void ValidateModeConsistency_WhenConfiguredModeIsLegacyAlias_DoesNotThrow(
        DeploymentMode suffixMode,
        string fileName,
        string configuredMode)
    {
        var exception = Record.Exception(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(suffixMode, fileName, configuredMode));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateModeConsistency_WhenConfiguredModeIsNullOrEmpty_DoesNotThrow(string? configuredMode)
    {
        var exception = Record.Exception(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(DeploymentMode.Edge, "appsettings.Production.Edge.json", configuredMode));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateModeConsistency_WhenConfiguredModeDiffers_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(
                DeploymentMode.Edge,
                "appsettings.Production.Edge.json",
                "Core"));

        Assert.Contains("appsettings.Production.Edge.json", ex.Message);
        Assert.Contains("Edge", ex.Message);
        Assert.Contains("Core", ex.Message);
    }

    [Fact]
    public void ValidateModeConsistency_WhenConfiguredModeDiffersViaLegacyAlias_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(
                DeploymentMode.Edge,
                "appsettings.Production.Edge.json",
                "Full"));

        Assert.Contains("appsettings.Production.Edge.json", ex.Message);
        Assert.Contains("Edge", ex.Message);
        Assert.Contains("Full", ex.Message);
    }

    [Fact]
    public void ValidateModeConsistency_WhenConfiguredModeIsInvalid_ThrowsInvalidOperationException()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeploymentModeFileLocator.ValidateModeConsistency(
                DeploymentMode.Edge,
                "appsettings.Production.Edge.json",
                "InvalidModeName"));

        Assert.Contains("appsettings.Production.Edge.json", ex.Message);
        Assert.Contains("Edge", ex.Message);
        Assert.Contains("InvalidModeName", ex.Message);
    }

    [Fact]
    public void ReadDeclaredMode_WhenFileDeclaresMode_ReturnsThatValue()
    {
        var path = Path.Combine(_tempDir, "appsettings.Production.Edge.json");
        File.WriteAllText(path, "{ \"Deployment\": { \"Mode\": \"Edge\" } }");

        Assert.Equal("Edge", DeploymentModeFileLocator.ReadDeclaredMode(path));
    }

    [Fact]
    public void ReadDeclaredMode_WhenFileOmitsMode_ReturnsNull()
    {
        // 後綴檔不寫模式鍵是本機制的正常用法：一致性檢查只能看這份檔案，
        // 不能看設定鏈（基底 appsettings.json 本來就宣告了另一個模式）
        var path = Path.Combine(_tempDir, "appsettings.Production.Edge.json");
        File.WriteAllText(path, "{ \"Line\": { \"OutboundHere\": true } }");

        Assert.Null(DeploymentModeFileLocator.ReadDeclaredMode(path));
    }

    [Fact]
    public void ValidateModeConsistency_WhenSuffixFileOmitsMode_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "appsettings.Production.Edge.json");
        File.WriteAllText(path, "{ \"Line\": { \"OutboundHere\": true } }");

        DeploymentModeFileLocator.ValidateModeConsistency(
            DeploymentMode.Edge,
            "appsettings.Production.Edge.json",
            DeploymentModeFileLocator.ReadDeclaredMode(path));
    }

    [Fact]
    public void Locate_WhenSuffixLooksLikeModeButUnparsable_ReportsItAsIgnored()
    {
        // 拼錯或用舊名時會靜默退回舊制，最難查的失敗形狀；至少要讓呼叫端拿得到檔名去提醒
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Edg.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Line.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
        Assert.Contains("appsettings.Production.Edg.json", result.IgnoredFiles);
        Assert.Contains("appsettings.Production.Line.json", result.IgnoredFiles);
    }

    [Fact]
    public void Locate_WhenSuffixIsCommaJoinedModes_IsIgnored()
    {
        // Enum.TryParse 會把 "Edge,Core" 解成合成值，不能讓它變成合法後綴
        File.WriteAllText(Path.Combine(_tempDir, "appsettings.Production.Edge,Core.json"), "{}");

        var result = DeploymentModeFileLocator.Locate(_tempDir, "Production");

        Assert.Equal(DeploymentModeFileLocatorStatus.None, result.Status);
    }
}

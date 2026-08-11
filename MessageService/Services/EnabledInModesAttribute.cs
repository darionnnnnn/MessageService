using MessageService.Options;

namespace MessageService.Services;

/// <summary>掛在 controller 上，宣告它只在哪些 <see cref="DeploymentMode"/> 下啟用。
/// 沒掛這個 attribute 的 controller 視為所有模式都啟用（例如 Web 專案沒有這套機制，
/// 這裡只影響 MessageService 自己的 controller）。實際生效見 <see cref="DeploymentModeConvention"/>。</summary>
[AttributeUsage(AttributeTargets.Class)]
public class EnabledInModesAttribute(params DeploymentMode[] modes) : Attribute
{
    public IReadOnlySet<DeploymentMode> Modes { get; } = modes.ToHashSet();
}

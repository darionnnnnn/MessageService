namespace MessageService.Services;

/// <summary>controller 需要哪個部署能力才存在，實際生效見 <see cref="DeploymentModeConvention"/>。
/// 用能力（Capability）而不是直接列舉 <see cref="MessageService.Options.DeploymentMode"/> 組合——
/// 能力可以被個別 override（例如 Core 模式關掉 Viewer），若直接寫死模式清單，override 後
/// controller 的存在與否就會跟能力的實際推導結果脫節。</summary>
public enum Capability
{
    Webhook,
    IngestApi,
    Viewer,
    EdgePullApi,
}

[AttributeUsage(AttributeTargets.Class)]
public class RequiresCapabilityAttribute(Capability capability) : Attribute
{
    public Capability Capability { get; } = capability;
}

using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace MessageService.Services;

/// <summary>依目前部署能力（見 DeploymentCapabilities）把不啟用的 controller 從
/// application model 整個移除——路由與 ApiExplorer 資訊一併消失，請求會落到一般的
/// 「找不到 endpoint」404，準確反映「這個模式沒有這個功能」而不是「有這個功能但被拒絕」。
///
/// 注意不能只清空 controller 的 Selectors：action 會被視為改用 conventional routing，
/// 但 [ApiController] 強制啟用 ApiExplorer、而 ApiExplorer 只支援 attribute routing，
/// 兩者衝突會讓整個 host 啟動就丟 InvalidOperationException（整合測試
/// DeploymentModeTests 就是為了釘住這類「單元測試看不到的路由內部行為」）。</summary>
public class DeploymentModeConvention(DeploymentCapabilities capabilities) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        for (var i = application.Controllers.Count - 1; i >= 0; i--)
        {
            var controllerType = application.Controllers[i].ControllerType;

            var requiresCapability = controllerType
                .GetCustomAttributes(typeof(RequiresCapabilityAttribute), inherit: true)
                .OfType<RequiresCapabilityAttribute>()
                .FirstOrDefault();
            if (requiresCapability is not null && !HasCapability(requiresCapability.Capability))
            {
                application.Controllers.RemoveAt(i);
            }
        }
    }

    private bool HasCapability(Capability capability) => capability switch
    {
        Capability.Webhook => capabilities.ReceivesWebhook,
        // IngestApiEnabled 已經同時涵蓋「模式是否允許」與「金鑰是否配置」兩個條件（見
        // DeploymentCapabilities.Derive）——不再需要獨立的 RequiresIngestApiKeyAttribute
        // 第二道閘門，兩者本來就只保護同一件事，分開反而讓「改一邊忘了改另一邊」有機可乘
        Capability.IngestApi => capabilities.IngestApiEnabled,
        Capability.Viewer => capabilities.ViewerEnabled,
        _ => false,
    };
}

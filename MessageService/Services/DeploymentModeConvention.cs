using MessageService.Options;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace MessageService.Services;

/// <summary>依目前部署模式把不啟用的 controller 從 application model 整個移除——路由與
/// ApiExplorer 資訊一併消失，請求會落到一般的「找不到 endpoint」404，準確反映
/// 「這個模式沒有這個功能」而不是「有這個功能但被拒絕」。
///
/// 注意不能只清空 controller 的 Selectors：action 會被視為改用 conventional routing，
/// 但 [ApiController] 強制啟用 ApiExplorer、而 ApiExplorer 只支援 attribute routing，
/// 兩者衝突會讓整個 host 啟動就丟 InvalidOperationException（整合測試
/// DeploymentModeTests 就是為了釘住這類「單元測試看不到的路由內部行為」）。</summary>
public class DeploymentModeConvention(DeploymentMode mode) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        for (var i = application.Controllers.Count - 1; i >= 0; i--)
        {
            var attribute = application.Controllers[i].ControllerType
                .GetCustomAttributes(typeof(EnabledInModesAttribute), inherit: true)
                .OfType<EnabledInModesAttribute>()
                .FirstOrDefault();

            if (attribute is not null && !attribute.Modes.Contains(mode))
            {
                application.Controllers.RemoveAt(i);
            }
        }
    }
}

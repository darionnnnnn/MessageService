using System.Reflection;
using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace MessageService.Tests.Services;

// convention 單元行為（controller 是否從 application model 被移除）在這裡驗；
// 「移除後請求真的 404、host 起得來」的整合結果在 DeploymentModeTests 用真實 host 驗——
// 兩層都要有：初版用「清空 Selectors」的做法就是單元測試全綠、真實 host 啟動就炸。
public class DeploymentModeConventionTests
{
    [EnabledInModes(DeploymentMode.Full, DeploymentMode.Line)]
    private class WebhookLikeController;

    private class UngatedController;

    private static ApplicationModel CreateApplication(params Type[] controllerTypes)
    {
        var application = new ApplicationModel();
        foreach (var type in controllerTypes)
        {
            var controller = new ControllerModel(type.GetTypeInfo(), []);
            controller.Selectors.Add(new SelectorModel());
            application.Controllers.Add(controller);
        }
        return application;
    }

    [Theory]
    [InlineData(DeploymentMode.Full)]
    [InlineData(DeploymentMode.Line)]
    public void Apply_ModeInAllowedSet_KeepsController(DeploymentMode mode)
    {
        var application = CreateApplication(typeof(WebhookLikeController));

        new DeploymentModeConvention(mode).Apply(application);

        Assert.Single(application.Controllers);
    }

    [Fact]
    public void Apply_ModeNotInAllowedSet_RemovesController()
    {
        var application = CreateApplication(typeof(WebhookLikeController), typeof(UngatedController));

        new DeploymentModeConvention(DeploymentMode.Db).Apply(application);

        var remaining = Assert.Single(application.Controllers);
        Assert.Equal(typeof(UngatedController).GetTypeInfo(), remaining.ControllerType);
    }

    [Theory]
    [InlineData(DeploymentMode.Full)]
    [InlineData(DeploymentMode.Line)]
    [InlineData(DeploymentMode.Db)]
    public void Apply_ControllerWithoutAttribute_NeverRemoved(DeploymentMode mode)
    {
        var application = CreateApplication(typeof(UngatedController));

        new DeploymentModeConvention(mode).Apply(application);

        Assert.Single(application.Controllers);
    }
}

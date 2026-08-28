using System.Reflection;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace MessageService.Tests.Services;

// convention 單元行為（controller 是否從 application model 被移除）在這裡驗；
// 「移除後請求真的 404、host 起得來」的整合結果在 DeploymentModeTests 用真實 host 驗——
// 兩層都要有：初版用「清空 Selectors」的做法就是單元測試全綠、真實 host 啟動就炸。
public class DeploymentModeConventionTests
{
    [RequiresCapability(Capability.Webhook)]
    private class WebhookLikeController;

    [RequiresCapability(Capability.IngestApi)]
    private class IngestLikeController;

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

    private static DeploymentCapabilities Capabilities(
        bool receivesWebhook = false,
        bool hasDatabaseAccess = false,
        bool ingestApiEnabled = false,
        bool viewerEnabled = false,
        bool outboundHere = false,
        bool runsRetention = false,
        bool edgePullApiEnabled = false) =>
        new(receivesWebhook, hasDatabaseAccess, ingestApiEnabled, viewerEnabled, outboundHere, runsRetention, edgePullApiEnabled);

    [Fact]
    public void Apply_CapabilityPresent_KeepsController()
    {
        var application = CreateApplication(typeof(WebhookLikeController));

        new DeploymentModeConvention(Capabilities(receivesWebhook: true)).Apply(application);

        Assert.Single(application.Controllers);
    }

    [Fact]
    public void Apply_CapabilityAbsent_RemovesController()
    {
        var application = CreateApplication(typeof(WebhookLikeController), typeof(UngatedController));

        new DeploymentModeConvention(Capabilities(receivesWebhook: false)).Apply(application);

        var remaining = Assert.Single(application.Controllers);
        Assert.Equal(typeof(UngatedController).GetTypeInfo(), remaining.ControllerType);
    }

    [Fact]
    public void Apply_ControllerWithoutAttribute_NeverRemoved()
    {
        var application = CreateApplication(typeof(UngatedController));

        // 全部能力都關掉，沒有任何 [RequiresCapability] 的 controller 仍應保留
        new DeploymentModeConvention(Capabilities()).Apply(application);

        Assert.Single(application.Controllers);
    }

    // ==== IngestApi 能力：已經同時涵蓋「模式是否允許」與「金鑰是否配置」，
    // 兩個條件只要有一個不成立，IngestApiEnabled 本身就會是 false ====

    [Fact]
    public void Apply_IngestApiEnabled_KeepsController()
    {
        var application = CreateApplication(typeof(IngestLikeController));

        new DeploymentModeConvention(Capabilities(ingestApiEnabled: true)).Apply(application);

        Assert.Single(application.Controllers);
    }

    [Fact]
    public void Apply_IngestApiDisabled_RemovesController()
    {
        var application = CreateApplication(typeof(IngestLikeController), typeof(UngatedController));

        new DeploymentModeConvention(Capabilities(ingestApiEnabled: false)).Apply(application);

        var remaining = Assert.Single(application.Controllers);
        Assert.Equal(typeof(UngatedController).GetTypeInfo(), remaining.ControllerType);
    }

    [Fact]
    public void Apply_MultipleControllers_EachJudgedIndependently()
    {
        var application = CreateApplication(
            typeof(WebhookLikeController), typeof(IngestLikeController), typeof(UngatedController));

        // Edge 模式的典型組合：收 webhook、沒有 ingest API、沒有能力限制的 controller 一律保留
        new DeploymentModeConvention(Capabilities(receivesWebhook: true, ingestApiEnabled: false)).Apply(application);

        var remainingTypes = application.Controllers.Select(c => c.ControllerType).ToHashSet();
        Assert.Contains(typeof(WebhookLikeController).GetTypeInfo(), remainingTypes);
        Assert.Contains(typeof(UngatedController).GetTypeInfo(), remainingTypes);
        Assert.DoesNotContain(typeof(IngestLikeController).GetTypeInfo(), remainingTypes);
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace MessageService.Tests.TestSupport;

/// <summary>TestServer 的請求沒有真正的 TCP 連線，Connection.RemoteIpAddress 預設是 null——
/// IP 白名單中介層（IngestIpAllowlistMiddleware）會把 null 一律當成拒絕，測試需要能控制
/// 「這個請求看起來從哪裡來」才能驗證白名單通過／拒絕兩種情境。跟
/// MessageService.Web.Tests/TestSupport/WebAppFactoryFixture.cs 裡的同名私有類別是同一個
/// 手法，這裡獨立成公開類別讓 MessageService.Tests 底下多個測試檔案共用。</summary>
public class FakeRemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = remoteIp;
            await nextMiddleware();
        });
        next(app);
    };
}

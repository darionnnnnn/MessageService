using System.Diagnostics;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;
using MessageService.Web.Models;

namespace MessageService.Web.Controllers;

// 只在檢視端能力開啟時才存在（見 DeploymentCapabilities.ViewerEnabled／DeploymentModeConvention）——
// 純 Edge 主機不該暴露首頁／錯誤頁
[RequiresCapability(Capability.Viewer)]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

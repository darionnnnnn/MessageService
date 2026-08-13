using System.Diagnostics;
using MessageService.Options;
using MessageService.Services;
using Microsoft.AspNetCore.Mvc;
using MessageService.Web.Models;

namespace MessageService.Web.Controllers;

// 只在有本機資料庫的模式下才存在（見 Program.cs 的 viewerEnabled／DeploymentModeConvention）——
// 純 Line／Edge 主機不該暴露首頁／錯誤頁
[EnabledInModes(DeploymentMode.Full, DeploymentMode.Db)]
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

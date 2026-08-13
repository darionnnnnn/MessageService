<#
.SYNOPSIS
    把 IIS 應用程式集區設成「一直開著」，避免背景服務（保留期清除／outbox 排空／
    媒體下載／頭貼刷新）被閒置逾時或固定間隔回收殺掉——這幾個是 BackgroundService，
    行程被回收時就整個停掉，見 docs/CONSOLIDATION-PLAN.md 問題1、docs/DEPLOYMENT-GUIDE.md
    的驗收清單（「隔天早上確認 log 出現 Retention cleanup removed」那條）。

    這些是 applicationHost.config 層級的設定，進不了專案裡的 web.config，只能用這支指令稿
    或 IIS 管理員手動設定固化下來；不管理任何機密或部署拓撲設定，那些在
    appsettings.Production.json（見同目錄的樣板）。

.PARAMETER AppPoolName
    要調整的應用程式集區名稱（必要）。

.PARAMETER SiteName
    要開啟 preloadEnabled 的網站名稱。省略則跳過這一步，只調整集區本身
    （AlwaysRunning／閒置逾時／固定間隔回收這三項已經足以避免行程被殺，
    preloadEnabled 是錦上添花——集區重新啟動後不用等第一個真實請求進來才啟動應用程式）。

.EXAMPLE
    .\Set-AppPool.ps1 -AppPoolName "MessageService" -SiteName "MessageService"

.NOTES
    在目標 Windows Server 上以系統管理員身分執行；本機開發機不需要跑這支。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPoolName,

    [Parameter(Mandatory = $false)]
    [string]$SiteName
)

$ErrorActionPreference = 'Stop'

Import-Module WebAdministration -ErrorAction Stop

$poolPath = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $poolPath)) {
    throw "找不到應用程式集區 '$AppPoolName'——請先在 IIS 管理員建好集區，或確認名稱正確。"
}

Write-Host "設定應用程式集區 '$AppPoolName'..." -ForegroundColor Cyan

# startMode=AlwaysRunning：集區啟動時就把應用程式帶起來，不等第一個請求
Set-ItemProperty -Path $poolPath -Name "startMode" -Value "AlwaysRunning"

# 閒置逾時歸零＝關閉「沒有請求就回收」——BackgroundService 不會產生 HTTP 請求，
# 半夜沒人傳訊息時預設 20 分鐘閒置逾時會把整個行程連同保留期清除、outbox 排空一起殺掉
Set-ItemProperty -Path $poolPath -Name "processModel.idleTimeout" -Value ([TimeSpan]::Zero)

# 固定間隔回收歸零＝關閉「跑滿 N 分鐘就強制回收」——預設 1740 分鐘（29 小時），
# 回收當下如果剛好在下載大檔案或跑保留期清除，會被腰斬重來
Set-ItemProperty -Path $poolPath -Name "recycling.periodicRestart.time" -Value ([TimeSpan]::Zero)

Write-Host "  startMode = AlwaysRunning" -ForegroundColor Green
Write-Host "  processModel.idleTimeout = 0（關閉閒置回收）" -ForegroundColor Green
Write-Host "  recycling.periodicRestart.time = 0（關閉固定間隔回收）" -ForegroundColor Green

if ($SiteName) {
    $sitePath = "IIS:\Sites\$SiteName"
    if (-not (Test-Path $sitePath)) {
        Write-Warning "找不到網站 '$SiteName'，略過 preloadEnabled 設定。"
    }
    else {
        Set-ItemProperty -Path $sitePath -Name "applicationDefaults.preloadEnabled" -Value $true
        Write-Host "  網站 '$SiteName' 的 preloadEnabled = true" -ForegroundColor Green
    }
}

# Application Initialization 是 preloadEnabled 真正生效的前提（沒裝這個角色服務，
# preloadEnabled 設了也不會有效果，集區還是要等第一個請求才真正啟動）；
# 用 ServerManager 模組偵測（Windows Server 專用，客戶端 SKU 沒有這個模組屬正常，不用理會）
try {
    Import-Module ServerManager -ErrorAction Stop
    $feature = Get-WindowsFeature -Name Web-AppInit -ErrorAction Stop
    if ($feature -and -not $feature.Installed) {
        Write-Warning "尚未安裝 IIS 的 Application Initialization 角色服務（Web-AppInit）——" +
            "preloadEnabled 不會真正生效，集區仍會等第一個請求才啟動應用程式。" +
            "可用「Install-WindowsFeature Web-AppInit」安裝，或透過『新增角色及功能』精靈勾選" +
            "『應用程式初始設定』。"
    }
}
catch {
    Write-Verbose "無法偵測 Application Initialization 角色服務安裝狀態（可能不是 Windows Server，或 ServerManager 模組不可用）——請自行確認。"
}

Write-Host "完成。建議隔天早上確認 log（logs\messageservice-*.log）出現「Retention cleanup removed ...」，" -ForegroundColor Cyan
Write-Host "確認保留期清除真的在半夜跑過，而不是行程被回收沒排到。" -ForegroundColor Cyan

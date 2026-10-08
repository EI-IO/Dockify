#Requires -Version 5.1
<#
    Dockify 构建脚本
    - 用 Windows 自带的 .NET Framework csc.exe 编译（不需要 .NET SDK，不需要联网）
    - 输出 dist\Dockify.exe（单文件，双击即可运行）
    - 顺手画一个 exe 图标（画失败也不影响编译）

    用法：  powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
    或直接双击 build.cmd
#>
[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$root  = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$srcFile = Join-Path $root 'src\Dockify.cs'
$distDir = Join-Path $root 'dist'
$buildDir = Join-Path $root 'build'
$outExe = Join-Path $distDir 'Dockify.exe'
$iconFile = Join-Path $buildDir 'app.ico'

function Say([string]$m) { if (-not $Quiet) { Write-Host $m } }

function Find-Csc {
    $candidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw '找不到 .NET Framework 的 csc.exe，无法编译。'
}

function New-AppIcon {
    param([string]$IconPath)

    Add-Type -AssemblyName System.Drawing

    $bmp = New-Object System.Drawing.Bitmap -ArgumentList @(32, 32)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bar = New-Object System.Drawing.RectangleF -ArgumentList @(2.0, 11.0, 28.0, 13.0)
    $d = 10.0
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddArc($bar.X, $bar.Y, $d, $d, 180, 90)
    $gp.AddArc($bar.Right - $d, $bar.Y, $d, $d, 270, 90)
    $gp.AddArc($bar.Right - $d, $bar.Bottom - $d, $d, $d, 0, 90)
    $gp.AddArc($bar.X, $bar.Bottom - $d, $d, $d, 90, 90)
    $gp.CloseFigure()

    $blue = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 62, 130, 255))
    $g.FillPath($blue, $gp)

    $white = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $g.FillEllipse($white, 7, 15, 4.5, 4.5)
    $g.FillEllipse($white, 14, 15, 4.5, 4.5)
    $g.FillEllipse($white, 21, 15, 4.5, 4.5)

    $g.Dispose(); $blue.Dispose(); $white.Dispose(); $gp.Dispose()

    if (-not (Test-Path $buildDir)) { New-Item -ItemType Directory -Force -Path $buildDir | Out-Null }

    $hicon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hicon)
    $fs = [System.IO.File]::Create($IconPath)
    try { $icon.Save($fs) } finally { $fs.Close() }
    $icon.Dispose()
    $bmp.Dispose()
    # 故意不调 DestroyIcon：脚本马上退出，句柄交给系统回收
}

# ------------------------------------------------------------------- 编译
if (-not (Test-Path $srcFile)) { throw "找不到源码：$srcFile" }
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Force -Path $distDir | Out-Null }

$csc = Find-Csc
Say "编译器: $csc"

# 先删旧产物：否则编译失败时会误把上一次的 exe 当成这次的结果
if (Test-Path $outExe) {
    try { Remove-Item $outExe -Force -ErrorAction Stop }
    catch { throw "dist\Dockify.exe 正被占用，请先退出正在运行的 Dockify（托盘右键 → 退出）再编译。" }
}

$haveIcon = $false
try {
    New-AppIcon -IconPath $iconFile
    $haveIcon = Test-Path $iconFile
}
catch {
    Write-Warning "生成图标失败（不影响功能）：$($_.Exception.Message)"
}

# 注意：PowerShell 里逗号运算符优先级高于 '+'，拼接项必须加括号
$common = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/codepage:65001',
    ('/out:' + $outExe),
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)
# UI Automation 引用（用来问 Windows 任务栏按钮的真实屏幕位置）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
$common += ('/r:' + [System.Windows.Automation.AutomationElement].Assembly.Location)
$common += ('/r:' + [System.Windows.Automation.AutomationElementIdentifiers].Assembly.Location)
$common += ('/r:' + [System.Windows.Rect].Assembly.Location)

if ($haveIcon) { $common += ('/win32icon:' + $iconFile) }

Say '编译中…'
$output = & $csc @common $srcFile 2>&1
$output | Where-Object { $_ -and $_.ToString().Trim().Length -gt 0 } | ForEach-Object { Write-Host $_ }

if (-not (Test-Path $outExe) -and $haveIcon) {
    Write-Warning '带图标编译失败，改用无图标重试…'
    $noIcon = $common | Where-Object { $_ -notlike '/win32icon:*' }
    $output = & $csc @noIcon $srcFile 2>&1
    $output | Where-Object { $_ -and $_.ToString().Trim().Length -gt 0 } | ForEach-Object { Write-Host $_ }
}

if (-not (Test-Path $outExe)) { throw '编译失败。' }

$fi = Get-Item $outExe
Say ''
Say ("完成: {0}  ({1:N0} 字节, {2})" -f $fi.FullName, $fi.Length, $fi.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
Write-Output $fi.FullName

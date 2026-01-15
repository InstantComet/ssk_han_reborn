# ======================================================
# SskCnPoc Build & Test Script
# ======================================================
# 用法:
#   .\build.ps1           # 默认 Release 构建
#   .\build.ps1 -Debug    # Debug 构建
#   .\build.ps1 -Run      # 构建后启动游戏
#   .\build.ps1 -Clean    # 清理后重新构建
# ======================================================

param(
    [switch]$Debug,
    [switch]$Run,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# 配置
$ProjectDir = "$PSScriptRoot\SskCnPoc"
$ProjectFile = "$ProjectDir\SskCnPoc.csproj"
$GameDir = "E:\Steam\steamapps\common\Sunless Skies"
$GameExe = "$GameDir\Sunless Skies.exe"
$PluginsDir = "$GameDir\BepInEx\plugins"
$Configuration = if ($Debug) { "Debug" } else { "Release" }

# 颜色输出函数
function Write-Step { param($msg) Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-OK { param($msg) Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Err { param($msg) Write-Host "   [ERROR] $msg" -ForegroundColor Red }
function Write-Info { param($msg) Write-Host "   $msg" -ForegroundColor Gray }

# 显示配置
Write-Host "============================================" -ForegroundColor Yellow
Write-Host "  SskCnPoc Build Script" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow
Write-Info "Configuration: $Configuration"
Write-Info "Project: $ProjectFile"
Write-Info "Game Dir: $GameDir"

# 检查项目文件
if (-not (Test-Path $ProjectFile)) {
    Write-Err "Project file not found: $ProjectFile"
    exit 1
}

# 清理 (如果需要)
if ($Clean) {
    Write-Step "Cleaning..."
    dotnet clean $ProjectFile -c $Configuration --verbosity minimal
    
    $binDir = "$ProjectDir\bin"
    $objDir = "$ProjectDir\obj"
    if (Test-Path $binDir) { Remove-Item -Recurse -Force $binDir }
    if (Test-Path $objDir) { Remove-Item -Recurse -Force $objDir }
    Write-OK "Clean completed"
}

# 还原依赖
Write-Step "Restoring packages..."
dotnet restore $ProjectFile --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Err "Restore failed!"
    exit 1
}
Write-OK "Packages restored"

# 构建
Write-Step "Building ($Configuration)..."
dotnet build $ProjectFile -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Err "Build failed!"
    exit 1
}
Write-OK "Build succeeded"

# 对于 Debug 模式，手动复制到插件目录
if ($Debug) {
    Write-Step "Copying to plugins folder (Debug mode)..."
    $dllPath = "$ProjectDir\bin\Debug\net6.0\SskCnPoc.dll"
    if (Test-Path $dllPath) {
        if (-not (Test-Path $PluginsDir)) {
            New-Item -ItemType Directory -Path $PluginsDir -Force | Out-Null
        }
        Copy-Item $dllPath $PluginsDir -Force
        Write-OK "Copied to $PluginsDir"
    } else {
        Write-Err "DLL not found: $dllPath"
    }
}

# 显示输出文件
Write-Step "Output files:"
$outputDir = "$ProjectDir\bin\$Configuration\net6.0"
if (Test-Path $outputDir) {
    Get-ChildItem $outputDir -Filter "*.dll" | ForEach-Object {
        Write-Info $_.FullName
    }
}

# 复制翻译文件到插件目录
Write-Step "Copying translation files..."
$translationFiles = @("ssk_cn.txt", "ssk_cn_missing.txt")
foreach ($file in $translationFiles) {
    $srcPath = "$PSScriptRoot\$file"
    if (Test-Path $srcPath) {
        Copy-Item $srcPath $PluginsDir -Force
        Write-OK "Copied $file"
    } else {
        Write-Info "Skipped $file (not found)"
    }
}

# 检查插件是否已部署
Write-Step "Plugin status:"
$pluginDll = "$PluginsDir\SskCnPoc.dll"
if (Test-Path $pluginDll) {
    $fileInfo = Get-Item $pluginDll
    Write-OK "Plugin deployed: $pluginDll"
    Write-Info "Last modified: $($fileInfo.LastWriteTime)"
} else {
    Write-Err "Plugin not found in game folder"
}

# 启动游戏 (如果需要)
if ($Run) {
    Write-Step "Starting game..."
    if (Test-Path $GameExe) {
        Write-Info "Launching: $GameExe"
        Start-Process $GameExe
        Write-OK "Game started"
    } else {
        Write-Err "Game executable not found: $GameExe"
        exit 1
    }
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host "  Build completed successfully!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green

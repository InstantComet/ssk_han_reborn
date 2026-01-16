# ======================================================
# SskCn Installer Build Script
# ======================================================
# 用法:
#   .\build-installer.ps1           # 构建安装程序
# ======================================================

param(
    [switch]$SkipPluginBuild
)

$ErrorActionPreference = "Stop"

# 配置
$RootDir = Split-Path $PSScriptRoot -Parent
$WorkingDir = "$RootDir\Working"
$InstallerDir = "$RootDir\Installer\SskCnInstaller"
$ResourcesDir = "$InstallerDir\Resources"
$PluginProject = "$WorkingDir\SskCnPoc\SskCnPoc.csproj"
$ParaDir = "$RootDir\para"
$FontOutputDir = "$RootDir\font_output"
$OutputDir = "$RootDir\Installer\Output"

# 颜色输出函数
function Write-Step { param($msg) Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-OK { param($msg) Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Err { param($msg) Write-Host "   [ERROR] $msg" -ForegroundColor Red }
function Write-Info { param($msg) Write-Host "   $msg" -ForegroundColor Gray }

Write-Host "============================================" -ForegroundColor Yellow
Write-Host "  SskCn Installer Build Script" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Yellow

# 步骤 1: 构建插件 (如果需要)
if (-not $SkipPluginBuild) {
    Write-Step "Building SskCnPoc plugin..."
    
    if (-not (Test-Path $PluginProject)) {
        Write-Err "Plugin project not found: $PluginProject"
        exit 1
    }
    
    Push-Location $WorkingDir
    try {
        dotnet build $PluginProject -c Release
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Plugin build failed!"
            exit 1
        }
        Write-OK "Plugin built successfully"
    } finally {
        Pop-Location
    }
}

# 步骤 2: 准备 Resources 目录
Write-Step "Preparing Resources..."

# 清理并创建 Resources 目录
if (Test-Path $ResourcesDir) {
    Remove-Item -Recurse -Force $ResourcesDir
}
New-Item -ItemType Directory -Path $ResourcesDir -Force | Out-Null
New-Item -ItemType Directory -Path "$ResourcesDir\para" -Force | Out-Null
New-Item -ItemType Directory -Path "$ResourcesDir\Fonts" -Force | Out-Null

# 复制插件 DLL
$dllPath = "$WorkingDir\SskCnPoc\bin\Release\net6.0\SskCnPoc.dll"
if (Test-Path $dllPath) {
    Copy-Item $dllPath "$ResourcesDir\SskCnPoc.dll"
    Write-OK "Copied SskCnPoc.dll"
} else {
    Write-Err "SskCnPoc.dll not found at: $dllPath"
    Write-Info "Run without -SkipPluginBuild to build the plugin first"
    exit 1
}

# 复制 para 翻译文件
if (Test-Path $ParaDir) {
    $jsonFiles = Get-ChildItem $ParaDir -Filter "*.json"
    foreach ($file in $jsonFiles) {
        Copy-Item $file.FullName "$ResourcesDir\para\"
    }
    Write-OK "Copied $($jsonFiles.Count) translation JSON files"
} else {
    Write-Err "Para directory not found: $ParaDir"
    exit 1
}

# 复制字体 AssetBundle
$fontBundle = "$FontOutputDir\sourcehan"
if (Test-Path $fontBundle) {
    Copy-Item $fontBundle "$ResourcesDir\Fonts\sourcehan"
    $size = [math]::Round((Get-Item $fontBundle).Length / 1MB, 2)
    Write-OK "Copied font AssetBundle (${size} MB)"
} else {
    Write-Err "Font AssetBundle not found: $fontBundle"
    exit 1
}

# 显示 Resources 内容
Write-Step "Resources prepared:"
Get-ChildItem $ResourcesDir -Recurse | ForEach-Object {
    $relativePath = $_.FullName.Replace($ResourcesDir, "").TrimStart('\')
    if ($_.PSIsContainer) {
        Write-Info "  [DIR] $relativePath"
    } else {
        $size = [math]::Round($_.Length / 1KB, 1)
        Write-Info "  $relativePath (${size} KB)"
    }
}

# 步骤 3: 构建安装程序
Write-Step "Building Installer..."

if (-not (Test-Path "$InstallerDir\SskCnInstaller.csproj")) {
    Write-Err "Installer project not found!"
    exit 1
}

# 创建输出目录
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Push-Location $InstallerDir
try {
    # 发布为单文件 exe
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $OutputDir
    
    if ($LASTEXITCODE -ne 0) {
        Write-Err "Installer build failed!"
        exit 1
    }
    Write-OK "Installer built successfully"
} finally {
    Pop-Location
}

# 步骤 4: 清理和重命名
Write-Step "Finalizing..."

# 删除 pdb 文件
Get-ChildItem $OutputDir -Filter "*.pdb" | Remove-Item -Force

# 重命名最终文件
$finalExe = "$OutputDir\SskCn汉化安装器.exe"
if (Test-Path $finalExe) {
    $size = [math]::Round((Get-Item $finalExe).Length / 1MB, 2)
    Write-OK "Final installer: $finalExe"
    Write-Info "Size: ${size} MB"
}

Write-Host "`n============================================" -ForegroundColor Green
Write-Host "  Build Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host "Output: $finalExe" -ForegroundColor White

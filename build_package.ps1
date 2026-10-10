param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [bool]$IncludeSingleFile = $true
)

$ErrorActionPreference = "Stop"
$rootDir = $PSScriptRoot

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Start building WeChat Copilot v$Version packages" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Ensure target directories
$publishDir = Join-Path $rootDir "publish\win-x64"
$singleFileDir = Join-Path $rootDir "publish\singlefile"
$distDir = Join-Path $rootDir "dist"

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# 2. Publish self-contained folder for Setup installer
Write-Host "`n[1/3] Publishing self-contained win-x64 app..." -ForegroundColor Yellow
$appProj = Join-Path $rootDir "src\WeChatCopilot.App\WeChatCopilot.App.csproj"
& dotnet publish $appProj -c $Configuration -r "win-x64" "--self-contained" "true" -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed!"
}

# 3. Compile Inno Setup installer
Write-Host "`n[2/3] Compiling Inno Setup installer..." -ForegroundColor Yellow
$isccPaths = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        $iscc = $cmd.Source
    }
}

if ($iscc) {
    $issFile = Join-Path $rootDir "installer\WeChatCopilot.iss"
    & $iscc "/DMyAppVersion=$Version" $issFile
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Inno Setup compilation failed."
    } else {
        Write-Host "Inno Setup installer created successfully!" -ForegroundColor Green
    }
} else {
    Write-Warning "ISCC.exe not found. Run 'winget install JRSoftware.InnoSetup' to enable installer generation."
}

# 4. Optional: build standalone portable single-file exe
if ($IncludeSingleFile) {
    Write-Host "`n[3/3] Building portable single-file executable..." -ForegroundColor Yellow
    & dotnet publish $appProj -c $Configuration -r "win-x64" "--self-contained" "true" "-p:PublishSingleFile=true" "-p:IncludeNativeLibrariesForSelfExtract=true" "-p:EnableCompressionInSingleFile=true" -o $singleFileDir

    if (Test-Path "$singleFileDir\WeChatCopilot.exe") {
        $portableTarget = Join-Path $distDir "WeChatCopilot_Portable_v$Version.exe"
        Copy-Item "$singleFileDir\WeChatCopilot.exe" $portableTarget -Force
        Write-Host "Portable single-file executable copied to dist folder!" -ForegroundColor Green
    }
}

# 5. Output summary
Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "  Build completed! Artifacts in dist folder:" -ForegroundColor Green
Get-ChildItem -Path $distDir | ForEach-Object {
    $sizeMB = [math]::Round($_.Length / 1MB, 2)
    Write-Host ("  - {0} ({1} MB)" -f $_.Name, $sizeMB) -ForegroundColor White
}
Write-Host "==========================================================" -ForegroundColor Cyan
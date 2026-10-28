# Собирает установщик WinDynamicNotch: self-contained publish + Inno Setup.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1

param(
    [switch]$SkipPublish,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'WinDynamicNotch.csproj'
$publishDir = Join-Path $root 'publish'
$distDir = Join-Path $root 'dist'

$dotnetCandidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
) | Where-Object { $_ -and (Test-Path $_) }
$dotnet = $dotnetCandidates | Select-Object -First 1

if (-not $dotnet) {
    throw 'Не найден dotnet. Установите .NET 9 SDK: winget install Microsoft.DotNet.SDK.9'
}

if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    Write-Host 'Публикация (self-contained, single-file)...'
    & $dotnet publish $project -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish завершился с кодом $LASTEXITCODE"
    }
}

$exe = Join-Path $publishDir 'WinDynamicNotch.exe'
if (-not (Test-Path $exe)) {
    throw "Не найден $exe — сначала выполните publish (без -SkipPublish)"
}

$isccCandidates = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw 'Не найден ISCC.exe. Установите Inno Setup 6: winget install JRSoftware.InnoSetup'
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Версию берём из csproj, чтобы exe и установщик не разъезжались.
[xml]$projectXml = Get-Content $project
$version = ($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) {
    $version = '1.0.0'
}

Write-Host "Сборка установщика (версия $version)..."
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'WinDynamicNotch.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC завершился с кодом $LASTEXITCODE"
}

$setup = Get-ChildItem $distDir -Filter '*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$sizeMb = [Math]::Round($setup.Length / 1MB, 1)

Write-Host ''
Write-Host "Готово: $($setup.FullName) ($sizeMb МБ)"

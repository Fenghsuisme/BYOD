<#
.SYNOPSIS
    下載 Monaco Editor 並將 min/vs 佈署到 Assets\webapp\vs（本地資源，不外連 CDN）。

.DESCRIPTION
    以 npm registry 的 tarball 直接下載指定版本的 monaco-editor，解壓後只取用
    package\min\vs，複製到專案的 Assets\webapp\vs。全程不需安裝 Node.js / npm。

.PARAMETER Version
    要下載的 monaco-editor 版本，預設 0.52.2。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Scripts\fetch-monaco.ps1
#>

param(
    [string]$Version = "0.52.2"
)

$ErrorActionPreference = "Stop"

# 專案根目錄 = 此腳本的上一層
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$targetVs = Join-Path $projectRoot "Assets\webapp\vs"

Write-Host "Monaco Editor 版本：$Version"
Write-Host "目標資料夾：$targetVs"

$tmp = Join-Path $env:TEMP ("monaco_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $tgzUrl = "https://registry.npmjs.org/monaco-editor/-/monaco-editor-$Version.tgz"
    $tgzPath = Join-Path $tmp "monaco.tgz"

    Write-Host "下載中：$tgzUrl"
    Invoke-WebRequest -Uri $tgzUrl -OutFile $tgzPath -UseBasicParsing

    # Windows 10+ 內建 tar
    Write-Host "解壓中…"
    tar -xzf $tgzPath -C $tmp

    $srcVs = Join-Path $tmp "package\min\vs"
    if (-not (Test-Path $srcVs)) {
        throw "找不到 $srcVs，套件結構可能已變更。"
    }

    if (Test-Path $targetVs) {
        Write-Host "清除舊的 vs 資料夾…"
        Get-ChildItem -Path $targetVs -Force |
            Where-Object { $_.Name -ne "PLACE_MONACO_HERE.md" } |
            Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $targetVs | Out-Null
    }

    Write-Host "複製 min\vs → Assets\webapp\vs …"
    Copy-Item -Path (Join-Path $srcVs "*") -Destination $targetVs -Recurse -Force

    Write-Host "完成。Monaco 本地資源已就位。" -ForegroundColor Green
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

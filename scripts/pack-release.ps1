# PickUp Audio(架上知音) 便携版打包脚本 / Release packaging script
#
# 用法 / Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/pack-release.ps1 `
#       -EsptoolZip <esptool-vX.Y-windows-amd64.zip> [-Version v0.17.0] [-SkipFirmware]
#
# 产出 / Output:
#   AudioCtrl\publish\           便携版目录(exe + Firmware\ + Tools\)
#   AudioCtrl-portable-<ver>.zip 绿色免安装包
#
# 说明:
#   - Firmware\: C3 三镜像(bootloader/partitions/firmware.bin),来自 PlatformIO 构建产物
#   - Tools\esptool.exe: esptool 独立版(GPLv2+,THIRD-PARTY-NOTICES 已登记);
#     不提供时发布包不含烧录器,上位机一键烧录回退到 python -m esptool
param(
    [string]$Version = "",
    [string]$FirmwareDir = "$PSScriptRoot\..\firmware-c3\.pio\build\esp32c3",
    [string]$EsptoolZip = "",
    [string]$EsptoolExe = "",
    [switch]$SkipFirmware
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $repo "AudioCtrl\publish"

# ---- 0) 版本单一来源校验 ----
$declaredVersion = $null
$versionFile = Join-Path $repo "Directory.Build.props"
if (Test-Path $versionFile) {
    [xml]$versionProps = Get-Content $versionFile
    $declaredVersion = @($versionProps.Project.PropertyGroup.Version) |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -ne "" -and $declaredVersion -and
    $Version.TrimStart('v') -ne $declaredVersion) {
    throw "版本不一致: -Version $Version, Directory.Build.props 为 $declaredVersion"
}

# ---- 1) 上位机(自包含单文件) ----
# 注意 IncludeSourceRevisionInInformationalVersion=false: 不传的话 .NET SDK 会把
# git 提交哈希嵌进 exe 的 ProductVersion(v1 发布时审计发现的隐私点,勿删)
# 清理上次运行可能留下的运行期产物/旧符号/临时解压目录,避免被递归收集进 zip
foreach ($stale in @((Join-Path $publish "Config"), (Join-Path $publish "Logs"),
                     (Join-Path $publish "Tools\_z"))) {
    if (Test-Path $stale) { Remove-Item $stale -Recurse -Force }
}
if (Test-Path $publish) {
    Get-ChildItem $publish -Filter *.pdb -File -Recurse | Remove-Item -Force
}

Write-Host "== dotnet publish =="
dotnet publish (Join-Path $repo "AudioCtrl\AudioCtrl.csproj") `
    -c Release -r win-x64 --self-contained -p:PublishSingleFile=true `
    -p:IncludeSourceRevisionInInformationalVersion=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# ---- 1b) 随包分发文件(MIT 要求附许可副本;说明文件面向解压即用的普通用户) ----
Write-Host "== 附带 LICENSE / NOTICES / 使用说明 =="
Copy-Item (Join-Path $repo "LICENSE") $publish -Force
Copy-Item (Join-Path $repo "THIRD-PARTY-NOTICES.md") $publish -Force
$guide = Join-Path $PSScriptRoot "使用说明.txt"
if (Test-Path $guide) { Copy-Item $guide $publish -Force }
else { Write-Warning "缺少 scripts\使用说明.txt:包内将没有中文快速上手说明" }

# ---- 2) 固件三镜像 ----
if (-not $SkipFirmware) {
    Write-Host "== 组装 Firmware\ =="
    $fw = Join-Path $publish "Firmware"
    New-Item -ItemType Directory -Force -Path $fw | Out-Null
    $FirmwareDir = [IO.Path]::GetFullPath($FirmwareDir)
    foreach ($f in @("bootloader.bin", "partitions.bin", "firmware.bin")) {
        $src = Join-Path $FirmwareDir $f
        if (!(Test-Path $src)) { throw "固件镜像缺失: $src (先 pio run -d firmware-c3)" }
        Copy-Item $src (Join-Path $fw $f) -Force
    }
}

# ---- 3) esptool.exe 独立版 ----
Write-Host "== 组装 Tools\ =="
$tools = Join-Path $publish "Tools"
New-Item -ItemType Directory -Force -Path $tools | Out-Null
if ($EsptoolExe -ne "" -and (Test-Path $EsptoolExe)) {
    Copy-Item $EsptoolExe (Join-Path $tools "esptool.exe") -Force
    Write-Host "  esptool.exe 已放入(来自指定路径)"
}
elseif ($EsptoolZip -ne "" -and (Test-Path $EsptoolZip)) {
    $tmp = Join-Path $tools "_z"
    Expand-Archive $EsptoolZip -DestinationPath $tmp -Force
    $e = Get-ChildItem $tmp -Recurse -Filter "esptool.exe" | Select-Object -First 1
    if ($null -eq $e) { throw "esptool 压缩包中未找到 esptool.exe" }
    Copy-Item $e.FullName (Join-Path $tools "esptool.exe") -Force
    Remove-Item $tmp -Recurse -Force
    Write-Host "  esptool.exe 已放入(来自 zip)"
}
elseif (Test-Path (Join-Path $tools "esptool.exe")) {
    Write-Host "  esptool.exe 已存在,保留(未提供 -EsptoolZip/-EsptoolExe)"
}
else {
    Write-Warning "未提供 esptool(-EsptoolZip/-EsptoolExe):发布包不含烧录器,"
    Write-Warning "上位机一键烧录将回退到 python -m esptool(要求已 pip install esptool)。"
}

# ---- 4) 打 zip ----
# 逐条目写入而非 CreateFromDirectory: ①中文文件名带 EFS/UTF-8 位(PS5.1 的
# Compress-Archive 不设,其他平台解压乱码——v1 教训);②条目名归一化为正斜杠
# (ZIP 规范要求 /,Windows 路径的 \ 在 Linux/macOS 严格工具下会变成字面文件名)
$ver = if ($Version -ne "") { $Version } else { "dev" }
$zip = Join-Path $repo "AudioCtrl-portable-$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [IO.File]::Open($zip, 'Create')
try {
    $archive = New-Object System.IO.Compression.ZipArchive($fs, 'Create')
    try {
        Get-ChildItem $publish -Recurse -File | Where-Object {
            $_.Extension -ne '.pdb' -and
            $_.FullName -notmatch '\\Tools\\_z\\' -and
            $_.FullName -notmatch '\\Config\\' -and
            $_.FullName -notmatch '\\Logs\\'
        } | ForEach-Object {
            $rel = $_.FullName.Substring($publish.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $_.FullName, $rel, 'Optimal') | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $fs.Dispose() }
Write-Host ("`n完成: {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

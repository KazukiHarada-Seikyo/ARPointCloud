# 端末から撮影データを取り出す。
#
#   .\tools\pull.ps1          … 全部
#   .\tools\pull.ps1 -Latest  … いちばん新しい rec_ フォルダだけ
#
# どのフォルダから実行しても、保存先はこのスクリプトから見た
# プロジェクト直下の Captures\ に固定される。
# (カレントフォルダ任せにすると system32 などに落ちて迷子になるため)

param(
    [switch]$Latest
)

$ErrorActionPreference = 'Stop'

# --- adb を探す ------------------------------------------------
$adb = $null

$fromPath = Get-Command adb -ErrorAction SilentlyContinue
if ($fromPath) {
    $adb = $fromPath.Source
} else {
    # Unity に同梱されているものを使う。バージョン違いも拾えるよう総当たり
    $roots = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ANDROID_HOME\platform-tools\adb.exe"
    )
    $roots += Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory -ErrorAction SilentlyContinue |
              ForEach-Object { Join-Path $_.FullName 'Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe' }

    foreach ($r in $roots) {
        if ($r -and (Test-Path $r)) { $adb = $r; break }
    }
}

if (-not $adb) {
    Write-Error "adb.exe が見つかりません。Unity の Android Build Support が入っているか確認してください"
}

Write-Host "adb: $adb"

# --- 接続確認 --------------------------------------------------
$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' }
if (-not $devices) {
    Write-Error "端末が見つかりません。USBを挿して、端末側でUSBデバッグを許可してください"
}
Write-Host "端末: $($devices -join ', ')"

# --- 取り出し --------------------------------------------------
$projectRoot = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $projectRoot 'Captures'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$remoteBase = '/sdcard/Android/data/jp.seikyo.arpointcloud/files'

if ($Latest) {
    $recs = & $adb shell "ls -d $remoteBase/rec_* 2>/dev/null" | Where-Object { $_ -match 'rec_' }
    if (-not $recs) { Write-Error "端末に rec_ フォルダがありません" }
    $target = ($recs | Sort-Object)[-1].Trim()
    Write-Host "取り出し: $target"
    & $adb pull $target $dest
} else {
    Write-Host "取り出し: $remoteBase (全部)"
    & $adb pull $remoteBase $dest
}

# --- 結果 ------------------------------------------------------
Write-Host ""
Write-Host "保存先: $dest"
Write-Host ""
Get-ChildItem $dest -Directory | ForEach-Object {
    $s = Get-ChildItem $_.FullName -File -Recurse | Measure-Object -Sum Length
    "{0,-24} {1,5} files {2,8:N0} MB" -f $_.Name, $s.Count, ($s.Sum / 1MB)
}

# Установка TrustTunnel GUI одной командой (PowerShell):
#   irm https://raw.githubusercontent.com/un1t-h/trusttunnel-gui/main/install.ps1 | iex
#
# Скачивает последнюю версию из GitHub Releases в %LOCALAPPDATA%\Programs\TrustTunnelGUI,
# создаёт ярлыки в меню «Пуск» и на рабочем столе. Повторный запуск обновляет программу.
# Удаление:  $env:TTGUI_UNINSTALL=1; irm https://raw.githubusercontent.com/un1t-h/trusttunnel-gui/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'
$repo = 'un1t-h/trusttunnel-gui'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\TrustTunnelGUI'
$exe = Join-Path $dir 'TrustTunnelGUI.exe'
$startLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'TrustTunnel.lnk'
$deskLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TrustTunnel.lnk'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

if ($env:TTGUI_UNINSTALL) {
    Remove-Item Env:\TTGUI_UNINSTALL
    if (Get-Process TrustTunnelGUI -ErrorAction SilentlyContinue) { throw 'Сначала закройте TrustTunnel GUI (значок в трее → Выход).' }
    Remove-Item $startLnk, $deskLnk -Force -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Say 'TrustTunnel GUI удалён. Профили остались в %APPDATA%\TrustTunnelGUI — удалите папку вручную, если они не нужны.' Green
    return
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Say 'TrustTunnel GUI — установка' Cyan
Say 'Получаю сведения о последней версии…'
$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'trusttunnel-gui-installer' }
$asset = $rel.assets | Where-Object { $_.name -eq 'TrustTunnelGUI.exe' } | Select-Object -First 1
if (-not $asset) { throw "В релизе $($rel.tag_name) нет файла TrustTunnelGUI.exe" }

New-Item -ItemType Directory -Force $dir | Out-Null
$tmp = "$exe.download"
Say "Скачиваю $($rel.tag_name) ($([math]::Round($asset.size / 1KB)) КБ)…"
Invoke-WebRequest $asset.browser_download_url -OutFile $tmp -UseBasicParsing

# Запущенный exe нельзя перезаписать, но можно переименовать — обновление без закрытия программы.
if (Test-Path $exe) {
    Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue
    Rename-Item $exe "$exe.old"
}
Move-Item $tmp $exe
Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue

$ws = New-Object -ComObject WScript.Shell
foreach ($path in @($startLnk, $deskLnk)) {
    $l = $ws.CreateShortcut($path)
    $l.TargetPath = $exe
    $l.WorkingDirectory = $dir
    $l.Description = 'TrustTunnel VPN'
    $l.Save()
}

Say "Готово: TrustTunnel GUI $($rel.tag_name) установлен в $dir" Green
Say 'Ярлыки «TrustTunnel» — в меню «Пуск» и на рабочем столе. При запуске Windows попросит права администратора: они нужны для VPN-адаптера.'
Say 'При первом запуске программа предложит скачать официальный клиент TrustTunnel — согласитесь.'
if (-not (Get-Process TrustTunnelGUI -ErrorAction SilentlyContinue)) {
    try { Start-Process $exe } catch { Say 'Запуск отменён — откройте программу ярлыком, когда будете готовы.' Yellow }
}

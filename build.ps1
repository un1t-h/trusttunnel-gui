# Сборка TrustTunnel GUI встроенным в Windows компилятором C# (.NET Framework 4.x) — ничего устанавливать не нужно.
#   .\build.ps1             — bin\TrustTunnelGUI.exe (запрашивает права администратора)
#   .\build.ps1 -NoElevate  — сборка без запроса UAC (для отладки интерфейса)
#   .\build.ps1 -Test       — дополнительно собрать и прогнать self-test (ссылки tt://, TOML)
param([switch]$NoElevate, [switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "Не найден csc.exe (.NET Framework 4.x)" }

$bin = Join-Path $root 'bin'
$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $bin, $obj | Out-Null

# --- иконка приложения (генерируется один раз) ---
$ico = Join-Path $obj 'app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $iconSrc = Get-Content (Join-Path $root 'src\Icons.cs') -Raw
    Add-Type -TypeDefinition ($iconSrc + @"

namespace TrustTunnelGui { public enum VpnState { Disconnected, Connecting, Connected, Disconnecting, Error } }
"@) -ReferencedAssemblies System.Drawing
    $sizes = 256, 48, 32, 16
    $pngs = foreach ($s in $sizes) {
        $bmp = [TrustTunnelGui.Icons]::DrawShield([TrustTunnelGui.Icons]::Accent, $s)
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($p in $pngs) { $w.Write($p) }
    $w.Flush()
    [IO.File]::WriteAllBytes($ico, $out.ToArray())
}

# --- манифест ---
$level = if ($NoElevate) { 'asInvoker' } else { 'requireAdministrator' }
$manifest = Join-Path $obj 'app.manifest'
(Get-Content (Join-Path $root 'app.manifest') -Raw).Replace('__LEVEL__', $level) | Set-Content $manifest -Encoding UTF8

$refs = 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
        'System.Security.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll' |
        ForEach-Object { "/r:$fw\$_" }
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$exe = Join-Path $bin 'TrustTunnelGUI.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu "/out:$exe" "/win32icon:$ico" "/win32manifest:$manifest" @refs @sources
if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции" }
Write-Host "OK: $exe ($level)"

if ($Test) {
    $testExe = Join-Path $obj 'SelfTest.exe'
    $libs = 'Profile.cs', 'DeepLink.cs', 'Toml.cs', 'ConfigBuilder.cs' | ForEach-Object { Join-Path $root "src\$_" }
    & $csc /nologo /target:exe /codepage:65001 "/out:$testExe" @refs @libs (Join-Path $root 'tests\SelfTest.cs')
    if ($LASTEXITCODE -ne 0) { throw "Ошибка компиляции тестов" }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Тесты не прошли" }
}

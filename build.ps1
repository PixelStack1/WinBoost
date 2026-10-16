# Build & publish WinBoost on Windows (PowerShell). Requires .NET 8 SDK (x64).
# Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1 [-OutDir "$env:USERPROFILE\Desktop\WinBoost"]
#
# Result: <OutDir>\WinBoost.exe is the only VISIBLE item; the ~440 runtime files sit next to it with the
# Hidden attribute (Explorer hides them by default). Why not a subfolder / single-file?
#  - PublishSingleFile: WinUI 3 unpackaged fails at startup (COMException 0x80040111, ClassFactory) because
#    reg-free WinRT activation + resources.pri/.xbf must be beside the exe.
#  - Moving DLLs to a subfolder needs probing/manifest rewrites + DLL-directory hacks — fragile across SDK updates.
# Tradeoff: with "Show hidden files" enabled in Explorer the runtime files appear (dimmed). Nothing else changes.
param([string]$OutDir = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinBoost'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Get-Process WinBoost -ErrorAction SilentlyContinue) { throw 'WinBoost is running - close it first.' }
if (Test-Path $OutDir) { Get-ChildItem $OutDir -Force | Remove-Item -Recurse -Force }
dotnet publish .\WinBoost\WinBoost.csproj -c Release -r win-x64 -p:Platform=x64 -o $OutDir
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
Remove-Item (Join-Path $OutDir '*.pdb') -ErrorAction SilentlyContinue
# Hide everything at the top level except the exe (sub-folders hidden as a whole).
Get-ChildItem $OutDir -Force | Where-Object { $_.Name -ne 'WinBoost.exe' } | ForEach-Object {
    $_.Attributes = $_.Attributes -bor [IO.FileAttributes]::Hidden
}
$exe = Join-Path $OutDir 'WinBoost.exe'
# Desktop shortcut (optional convenience)
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinBoost.lnk'))
$s.TargetPath = $exe; $s.WorkingDirectory = $OutDir; $s.IconLocation = "$exe,0"; $s.Description = 'WinBoost - by Corpse'; $s.Save()
Write-Host "`nOK -> $exe" -ForegroundColor Green
# Optional installer (Inno Setup 6):  & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DPublishDir="$OutDir" .\WinBoost.iss

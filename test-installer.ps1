param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build-tests\installer'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-installer.ps1') -OutputDirectory $OutputDirectory -TestInstall
$target = Join-Path $OutputDirectory 'installed-test'
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CheckUpdateInstallerTest_is1'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Check Update Installer Test.lnk'
$desktopPath = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Check Update Installer Test.lnk'
if ((Test-Path -LiteralPath $registryPath) -or (Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $shortcutPath) -or (Test-Path -LiteralPath $desktopPath)) {
    throw 'Previous installer test files exist; refusing to overwrite them'
}
$installer = Join-Path $OutputDirectory 'Check-Update-Setup.exe'
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/DESKTOP=1', ('/DIR="' + $target + '"'), ('/LOG="' + (Join-Path $OutputDirectory 'install.log') + '"'))
$install = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
try {
    if ($install.ExitCode -ne 0) { throw ('Installation failed: ' + $install.ExitCode) }
    $installedExe = Join-Path $target 'Check Update.exe'
    if (-not (Test-Path -LiteralPath $installedExe)) { throw 'Application file was not installed' }
    if ((Get-FileHash -LiteralPath $installedExe).Hash -ne (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'Check Update.exe')).Hash) { throw 'Installed file hash mismatch' }
    if (-not (Test-Path -LiteralPath $registryPath)) { throw 'Uninstall entry was not created' }
    $registration = Get-ItemProperty -LiteralPath $registryPath
    if ($registration.DisplayVersion -ne '0.5.0') { throw 'Incorrect installed version' }
    foreach ($shortcut in @($shortcutPath, $desktopPath)) {
        if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Expected shortcut was not created' }
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        if ($link.TargetPath -ne $installedExe) { throw 'Shortcut target mismatch' }
    }
    [IO.File]::WriteAllText((Join-Path $target 'Check Update.exe.previous'), 'test backup')
    Write-Output 'PASS per-user installation, payload hash, version registration, Start menu and desktop shortcuts'
} finally {
    $uninstaller = Join-Path $target 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $OutputDirectory 'uninstall.log') + '"')) -Wait -PassThru -WindowStyle Hidden
        if ($uninstall.ExitCode -ne 0) { throw 'Uninstallation failed' }
    }
}
if ((Test-Path -LiteralPath $registryPath) -or (Test-Path -LiteralPath $shortcutPath) -or (Test-Path -LiteralPath $desktopPath) -or (Test-Path -LiteralPath (Join-Path $target 'Check Update.exe')) -or (Test-Path -LiteralPath (Join-Path $target 'Check Update.exe.previous'))) {
    throw 'Uninstaller did not clean registered application files and shortcuts'
}
Write-Output 'PASS uninstallation removes registered files, backup, shortcuts and uninstall entry'

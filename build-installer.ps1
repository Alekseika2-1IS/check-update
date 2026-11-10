param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'), [switch]$TestInstall)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $OutputDirectory
$compilerRoot = Join-Path $env:LOCALAPPDATA 'CheckUpdateBuild\InnoSetup6'
$compiler = Join-Path $compilerRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $downloadDirectory = Join-Path $env:LOCALAPPDATA 'CheckUpdateBuild\downloads'
    New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null
    $toolInstaller = Join-Path $downloadDirectory 'innosetup-6.7.4.exe'
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $toolInstaller
    $signature = Get-AuthenticodeSignature -LiteralPath $toolInstaller
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
        throw 'Inno Setup publisher verification failed'
    }
    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', '/TASKS=""', ('/DIR="' + $compilerRoot + '"'))
    $installProcess = Start-Process -FilePath $toolInstaller -ArgumentList $installArguments -Wait -PassThru -WindowStyle Hidden
    if ($installProcess.ExitCode -ne 0) { throw 'Could not prepare installer compiler' }
}
$appVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $OutputDirectory 'Check Update.exe')).Version.ToString(3)
$arguments = @('/Qp', ('/DAppVersion=' + $appVersion), ('/DBuildDir=' + $OutputDirectory))
if ($TestInstall) { $arguments += '/DTestInstall=1' }
$arguments += Join-Path $PSScriptRoot 'setup.iss'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
Write-Output (Join-Path $OutputDirectory 'Check-Update-Setup.exe')

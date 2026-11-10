param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$testDirectory = Join-Path $PSScriptRoot 'build-tests'
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $testDirectory
$references = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArguments = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + (Join-Path $testDirectory 'Tests.exe')), ('/reference:' + (Join-Path $testDirectory 'Check Update.exe')))
foreach ($reference in $references) { $compilerArguments += '/reference:' + (Join-Path $framework $reference) }
$compilerArguments += Join-Path $PSScriptRoot 'Tests.cs'
& (Join-Path $framework 'csc.exe') @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
$testArguments = @()
if ($Offline) { $testArguments += '--offline' }
& (Join-Path $testDirectory 'Tests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

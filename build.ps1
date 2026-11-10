param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Runtime.Serialization.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', ('/out:' + (Join-Path $outputDirectory 'Check Update.exe')))
$compilerArguments += '/win32icon:' + (Join-Path $PSScriptRoot 'assets\app.ico')
$compilerArguments += '/resource:' + (Join-Path $PSScriptRoot 'assets\app.png') + ',CheckUpdate.Icon.png'
$compilerArguments += '/resource:' + (Join-Path $PSScriptRoot 'Theme.xaml') + ',CheckUpdate.Theme.xaml'
foreach ($reference in $references) { $compilerArguments += '/reference:' + (Join-Path $framework $reference) }
$compilerArguments += Join-Path $PSScriptRoot 'Program.cs'
$compilerArguments += Join-Path $PSScriptRoot 'SelfUpdate.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Design.cs'
& (Join-Path $framework 'csc.exe') @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output (Join-Path $outputDirectory 'Check Update.exe')

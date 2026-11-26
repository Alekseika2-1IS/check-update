param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$references = @('System.dll', 'System.Core.dll', 'System.Management.dll', 'System.Net.Http.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Xaml.dll', 'Microsoft.CSharp.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compilerArguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', ('/out:' + (Join-Path $outputDirectory 'Check Update.exe')))
$compilerArguments += '/win32icon:' + (Join-Path $PSScriptRoot 'assets\app.ico')
$compilerArguments += '/resource:' + (Join-Path $PSScriptRoot 'assets\app.png') + ',CheckUpdate.Icon.png'
$compilerArguments += '/resource:' + (Join-Path $PSScriptRoot 'Theme.xaml') + ',CheckUpdate.Theme.xaml'
$fontResourcePath = Join-Path $outputDirectory 'Check Update.g.resources'
$fontResources = [System.Resources.ResourceWriter]::new($fontResourcePath)
try {
    foreach ($name in @('manrope-regular.ttf', 'manrope-semibold.ttf', 'manrope-bold.ttf', 'manrope-ofl.txt')) {
        $assetPath = Join-Path $PSScriptRoot ('assets\fonts\' + $name)
        $fontResources.AddResource(('fonts/' + $name), [IO.File]::OpenRead($assetPath), $true)
    }
}
finally { $fontResources.Dispose() }
$compilerArguments += '/resource:' + $fontResourcePath + ',Check Update.g.resources'
foreach ($reference in $references) { $compilerArguments += '/reference:' + (Join-Path $framework $reference) }
$compilerArguments += Join-Path $PSScriptRoot 'Program.cs'
$compilerArguments += Join-Path $PSScriptRoot 'SelfUpdate.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Design.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Confirmation.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Maintenance.cs'
$compilerArguments += Join-Path $PSScriptRoot 'History.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Journal.cs'
$compilerArguments += Join-Path $PSScriptRoot 'SystemUpdates.cs'
$compilerArguments += Join-Path $PSScriptRoot 'NvidiaUpdates.cs'
$compilerArguments += Join-Path $PSScriptRoot 'PackageIcons.cs'
$compilerArguments += Join-Path $PSScriptRoot 'Workflows.cs'
$compilerArguments += Join-Path $PSScriptRoot 'ButtonHelp.cs'
& (Join-Path $framework 'csc.exe') @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output (Join-Path $outputDirectory 'Check Update.exe')

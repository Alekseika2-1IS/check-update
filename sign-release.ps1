param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-installer.ps1') -OutputDirectory $OutputDirectory
$portable = Join-Path $OutputDirectory 'Check-Update.exe'
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'Check Update.exe') -Destination $portable
$assembly = [Reflection.Assembly]::LoadFile([IO.Path]::GetFullPath($portable))
$tag = 'v' + $assembly.GetName().Version.ToString(3)
$updater = $assembly.GetType('UpdateHub.SelfUpdate', $true)
$payload = $updater.GetMethod('SignaturePayload')
$verify = $updater.GetMethod('VerifyReleaseSignature')
$parameters = [System.Security.Cryptography.CspParameters]::new(24)
$parameters.KeyContainerName = 'CheckUpdate.ReleaseSigning.v1'
$parameters.Flags = [System.Security.Cryptography.CspProviderFlags]::UseExistingKey
$signer = [System.Security.Cryptography.RSACryptoServiceProvider]::new($parameters)
try {
    $publicKey = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'assets/release-public-key.xml')).Trim()
    if ($signer.CspKeyContainerInfo.Exportable -or $signer.ToXmlString($false) -ne $publicKey) {
        throw 'The local signing key does not match the bundled public key.'
    }
    foreach ($name in @('Check-Update.exe', 'Check-Update-Setup.exe')) {
        $file = Join-Path $OutputDirectory $name
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $message = [byte[]]$payload.Invoke($null, @($tag, $name, $hash))
        $signature = $signer.SignData($message, [System.Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        if (-not $verify.Invoke($null, @($tag, $name, $hash, $signature))) {
            throw "Signature verification failed for $name."
        }
        [IO.File]::WriteAllText($file + '.sha256', "$hash  $name`n", [Text.Encoding]::ASCII)
        [IO.File]::WriteAllBytes($file + '.sig', $signature)
        Write-Output "Signed and verified $name for $tag"
    }
}
finally { $signer.Dispose() }

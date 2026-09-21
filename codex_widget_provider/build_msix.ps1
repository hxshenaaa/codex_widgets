# Creates (once) a local self-signed code signing cert matching the package
# identity publisher (CN=CodexUsageWidgetDev), trusts it for the current user,
# then publishes the signed MSIX for sideloading.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$subject = 'CN=CodexUsageWidgetDev'
$pfxPath = Join-Path $root 'CodexUsageWidgetDev.pfx'
$cerPath = Join-Path $root 'CodexUsageWidgetDev.cer'
$password = 'CodexUsageWidget!2026'
$securePw = ConvertTo-SecureString $password -AsPlainText -Force

if (-not (Test-Path $pfxPath)) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation 'Cert:\CurrentUser\My'
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePw | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Write-Host "Created self-signed cert: $subject"
}

if (-not (Test-Path $cerPath)) {
    $cert = Get-PfxData -FilePath $pfxPath -Password $securePw
    Export-Certificate -Cert $cert.EndEntityCertificates[0] -FilePath $cerPath | Out-Null
}

# Trust the signing cert for the current user (no admin required).
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
Write-Host 'Cert trusted in Cert:\CurrentUser\TrustedPeople'

# Machine-level trust is what Add-AppxPackage actually checks; needed once per
# machine. Shows a UAC prompt.
if (-not (Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' -ErrorAction SilentlyContinue | Where-Object Subject -eq $subject)) {
    Write-Host 'Installing cert to Cert:\LocalMachine\TrustedPeople (approve the UAC prompt)...'
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile','-Command',"Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
}

# Sign with the cert thumbprint from the user's personal store.
$thumbprint = (Get-ChildItem 'Cert:\CurrentUser\My' | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1).Thumbprint
if (-not $thumbprint) { throw "No cert with subject '$subject' in Cert:\CurrentUser\My" }
Write-Host "Signing with thumbprint: $thumbprint"

$project = Join-Path $root 'CodexUsageWidgetProvider.csproj'
$appxDir = Join-Path $root 'AppPackages'

dotnet publish $project `
    -c Release -r win-x64 -p:Platform=x64 `
    '-p:PublishAppxPackage=true' `
    '-p:UapAppxPackageBuildMode=SideloadOnly' `
    '-p:AppxBundle=Never' `
    '-p:AppxPackageSigningEnabled=true' `
    "-p:PackageCertificateThumbprint=$thumbprint" `
    "-p:AppxPackageDir=$appxDir"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host ''
Write-Host 'Generated packages:'
Get-ChildItem (Join-Path $root 'AppPackages') -Recurse -Include *.msix,*.msixbundle -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  $($_.FullName)" }

<#
.SYNOPSIS
    Builds Wisland (the Winland app) into MSIX packages for Microsoft Store
    submission, without requiring Visual Studio's Windows Application
    Packaging Project / UWP workload  -  just the .NET SDK (already installed)
    and the Windows SDK's makeappx/signtool (already installed, found under
    Windows Kits\10\bin\<ver>\<arch>).

.DESCRIPTION
    For each requested architecture:
      1. `dotnet publish` the WPF app self-contained for that RID.
      2. Stage a package layout folder: publish output + Package.appxmanifest
         (renamed to AppxManifest.xml, with ProcessorArchitecture patched to
         match) + Assets\Store.
      3. Run makeappx.exe pack to produce Wisland_<version>_<arch>.msix.

    This does NOT sign the package. Store submission does not require (and
    Partner Center will re-sign regardless of) a pre-existing signature  -  see
    STORE_SUBMISSION.md. Use -SignForLocalTesting only to sideload-test on
    your own machine before submitting; that signing has no bearing on the
    Store upload.

.PARAMETER Architectures
    One or more of: x64, arm64. Defaults to both  -  Store submissions
    typically include both so ARM64 devices get a native package.

.PARAMETER PackageVersion
    Four-part version, must match Identity/@Version in Package.appxmanifest
    once you've filled that in. Defaults to 1.0.0.0.

.EXAMPLE
    ./build/Package-Msix.ps1
    ./build/Package-Msix.ps1 -Architectures x64 -SignForLocalTesting
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string[]] $Architectures = @('x64', 'arm64'),

    [string] $PackageVersion = '1.0.0.0',

    [switch] $SignForLocalTesting
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\Winland\Winland.csproj'
$ManifestPath = Join-Path $RepoRoot 'src\Winland\Package.appxmanifest'
$StoreAssetsPath = Join-Path $RepoRoot 'src\Winland\Assets\Store'
$OutRoot = Join-Path $RepoRoot 'artifacts\msix'

if ((Get-Content $ManifestPath -Raw) -match 'TODO-PARTNER-CENTER') {
    Write-Warning "Package.appxmanifest still has TODO-PARTNER-CENTER placeholders (Identity Name/Publisher). The .msix this produces will build, but Partner Center will reject it on upload until those are filled in from your 'App identity' page. See STORE_SUBMISSION.md."
}

function Find-WindowsSdkTool {
    param([Parameter(Mandatory)][string] $ToolName)

    $kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
    $candidate = Get-ChildItem -Path $kitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$ToolName" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if (-not $candidate) {
        throw "Could not find $ToolName under $kitsRoot. Install the Windows SDK (or Visual Studio's 'Windows 10/11 SDK' component)."
    }
    return $candidate
}

$MakeAppx = Find-WindowsSdkTool -ToolName 'makeappx.exe'
Write-Host "Using makeappx: $MakeAppx"

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

foreach ($arch in $Architectures) {
    Write-Host "`n=== Building $arch ===" -ForegroundColor Cyan

    $publishDir = Join-Path $OutRoot "publish-$arch"
    $layoutDir = Join-Path $OutRoot "layout-$arch"
    $msixPath = Join-Path $OutRoot "Wisland_${PackageVersion}_${arch}.msix"

    Remove-Item -Recurse -Force $publishDir, $layoutDir -ErrorAction SilentlyContinue

    dotnet publish $ProjectPath `
        -c Release `
        -r "win-$arch" `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $arch" }

    New-Item -ItemType Directory -Force -Path $layoutDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $publishDir '*') $layoutDir

    # Store assets, keeping the same relative path the manifest references.
    $assetsDest = Join-Path $layoutDir 'Assets\Store'
    New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
    Copy-Item -Force (Join-Path $StoreAssetsPath '*') $assetsDest

    # Patch Identity/@Version and @ProcessorArchitecture per architecture,
    # then drop the result in as AppxManifest.xml (the name makeappx expects).
    [xml] $manifestXml = Get-Content $ManifestPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($manifestXml.NameTable)
    $ns.AddNamespace('a', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identityNode = $manifestXml.SelectSingleNode('//a:Identity', $ns)
    $identityNode.SetAttribute('Version', $PackageVersion)
    $identityNode.SetAttribute('ProcessorArchitecture', $arch)
    $manifestXml.Save((Join-Path $layoutDir 'AppxManifest.xml'))

    & $MakeAppx pack /d $layoutDir /p $msixPath /overwrite
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed for $arch" }

    Write-Host "Built $msixPath" -ForegroundColor Green

    if ($SignForLocalTesting) {
        $SignTool = Find-WindowsSdkTool -ToolName 'signtool.exe'
        $pfxPath = Join-Path $OutRoot 'local-test-cert.pfx'
        $pfxPassword = 'wisland-local-test'

        if (-not (Test-Path $pfxPath)) {
            Write-Host 'Creating a throwaway self-signed cert for local sideload testing only (not used for Store submission)...'
            $subject = "CN=$env:USERNAME-WislandLocalTest"
            $cert = New-SelfSignedCertificate -Type Custom -Subject $subject `
                -KeyUsage DigitalSignature -FriendlyName 'Wisland local test cert' `
                -CertStoreLocation 'Cert:\CurrentUser\My' `
                -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
            $securePwd = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
            Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
            Write-Host "Self-signed cert exported to $pfxPath (install it into 'Trusted People' on any machine you sideload to)."
        }

        & $SignTool sign /fd SHA256 /a /f $pfxPath /p $pfxPassword $msixPath
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $arch" }
        Write-Host "Signed $msixPath for local sideload testing." -ForegroundColor Yellow
    }
}

Write-Host "`nDone. Packages are in $OutRoot." -ForegroundColor Cyan
Write-Host "These are unsigned (unless -SignForLocalTesting) - Partner Center signs uploaded packages itself. See STORE_SUBMISSION.md for the rest of the submission checklist."

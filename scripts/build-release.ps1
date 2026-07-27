[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DeskHush.sln'
$appProjectPath = Join-Path $repositoryRoot 'src\DeskHush.App\DeskHush.App.csproj'
$testProjectPath = Join-Path $repositoryRoot 'tests\DeskHush.Tests\DeskHush.Tests.csproj'
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$publishDirectory = Join-Path $repositoryRoot "artifacts\publish\$RuntimeIdentifier"
$releaseDirectory = Join-Path $repositoryRoot 'artifacts\release'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath $propsPath
    $Version = @($props.Project.PropertyGroup.Version) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'No release version was supplied and Directory.Build.props contains no Version value.'
}

$archiveName = "DeskHush-v$Version-$RuntimeIdentifier.zip"
$archivePath = Join-Path $releaseDirectory $archiveName
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'

Push-Location $repositoryRoot
try {
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    if (Test-Path -LiteralPath $releaseDirectory) {
        Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

    # There are no third-party PackageReferences; Dependabot covers future dependency changes.
    Write-Host 'Restoring solution...'
    dotnet restore $solutionPath -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Solution restore failed with exit code $LASTEXITCODE." }

    Write-Host 'Building solution with warnings treated as errors...'
    dotnet build $solutionPath --configuration Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed with exit code $LASTEXITCODE." }

    Write-Host 'Running test executable...'
    dotnet run --project $testProjectPath --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }

    Write-Host "Restoring the $RuntimeIdentifier runtime pack..."
    dotnet restore $appProjectPath --runtime $RuntimeIdentifier -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Runtime restore failed with exit code $LASTEXITCODE." }

    Write-Host 'Publishing a self-contained single-file application...'
    dotnet publish $appProjectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --no-restore `
        --output $publishDirectory `
        -p:Version=$Version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    $executablePath = Join-Path $publishDirectory 'DeskHush.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Published executable was not found at $executablePath."
    }

    foreach ($fileName in @('LICENSE', 'README.md', 'README_EN.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $fileName) -Destination $publishDirectory
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination $publishDirectory -Recurse

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $archiveName" | Set-Content -LiteralPath $checksumPath -Encoding ascii

    Write-Host ''
    Write-Host "Release archive: $archivePath"
    Write-Host "SHA-256:         $archiveHash"
    Write-Host "Checksums:       $checksumPath"
}
finally {
    Pop-Location
}

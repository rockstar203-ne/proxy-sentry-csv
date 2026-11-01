param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$release = Join-Path $root 'release'
$portable = Join-Path $release 'ProxySentryCSV-portable'
$staging = Join-Path $root ('.qa\package-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $staging 'ProxySentryCSV-portable'
$components = Join-Path $package 'components'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location $root
try {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($portable + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if ($running) { throw 'Close the portable application before packaging. User browser windows are not stopped automatically.' }
    if (-not $SkipTests) {
        Invoke-DotNet @('run', '--project', 'ProxySentryCsv.Tests/ProxySentryCsv.Tests.csproj', '-c', 'Release')
    }
    New-Item -ItemType Directory -Force -Path $components, (Join-Path $package 'Data') | Out-Null
    Invoke-DotNet @('publish', 'ProxySentryCsv.WinUI/ProxySentryCsv.WinUI.csproj', '-c', 'Release', '-p:Platform=x64', '-r', 'win-x64', '--self-contained', 'true', '-o', $components)
    Invoke-DotNet @('publish', 'ProxySentryCsv.Launcher/ProxySentryCsv.Launcher.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $staging 'launcher'))
    Invoke-DotNet @('publish', 'ProxySentryCsv.Updater/ProxySentryCsv.Updater.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $staging 'updater'))
    Copy-Item -LiteralPath (Join-Path $staging 'launcher\Proxy Sentry CSV.exe') -Destination $package
    Copy-Item -LiteralPath (Join-Path $staging 'updater\ProxySentryCsv.Updater.exe') -Destination $components
    Copy-Item -LiteralPath (Join-Path $root 'ProxySentryCsv.WinUI\README-ProxySentryCSV.txt') -Destination $package
    foreach ($relative in @('Proxy Sentry CSV.exe', 'components\Proxy Sentry CSV.exe', 'components\ProxySentryCsv.Updater.exe', 'components\MailKit.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $relative))) { throw "Missing portable component: $relative" }
    }
    if (Get-ChildItem -LiteralPath (Join-Path $package 'Data') -Force) { throw 'Package Data must be empty.' }
    $zip = Join-Path $staging 'ProxySentryCSV-portable-win10-win11.zip'
    Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal

    # Copy only build artifacts; never package or replace the user's existing Data.
    New-Item -ItemType Directory -Force -Path (Join-Path $portable 'components'), (Join-Path $portable 'Data') | Out-Null
    Get-ChildItem -LiteralPath $components | Copy-Item -Destination (Join-Path $portable 'components') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $package 'Proxy Sentry CSV.exe'), (Join-Path $package 'README-ProxySentryCSV.txt') -Destination $portable -Force
    Copy-Item -LiteralPath $zip -Destination (Join-Path $release 'ProxySentryCSV-portable-win10-win11.zip') -Force
    Write-Output "Portable launcher: $(Join-Path $portable 'Proxy Sentry CSV.exe')"
    Get-FileHash -LiteralPath (Join-Path $release 'ProxySentryCSV-portable-win10-win11.zip') -Algorithm SHA256
}
finally {
    Pop-Location
    # Keep staging available for inspection if publishing or packaging fails.
}

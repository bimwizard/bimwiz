param([string]$RepositoryRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$framework = Split-Path -Parent $compiler
$source = Join-Path $repo 'BIMWiz.OnlineSetup.cs'
$logo = Join-Path $repo 'BIMWiz-logo.png'
$package = Join-Path $repo 'BIMWiz-Installer-Package.zip'
$executable = Join-Path $repo 'BIMWiz-Online-Setup.exe'
foreach ($required in @($compiler, $source, $logo, $package)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file is missing: $required" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $versions = @(@(foreach ($entry in $zip.Entries) {
        if ($entry.FullName -match '^payload/[0-9]{4}/version\.txt$') {
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
        }
    }) | Select-Object -Unique)
    if (@($versions).Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0])) {
        throw 'The package must contain one consistent release version across its Revit payloads.'
    }
} finally { $zip.Dispose() }

$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll',
    'System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& $compiler /nologo /noconfig /target:winexe /optimize+ /platform:anycpu "/out:$executable" "/resource:$logo,BIMWiz.logo" @references $source
if ($LASTEXITCODE -ne 0) { throw 'The online installer build failed.' }

$release = [ordered]@{
    schemaVersion = 1
    version = [string]$versions[0]
    packageUrl = 'https://raw.githubusercontent.com/bimwizard/bimwiz/main/BIMWiz-Installer-Package.zip'
    sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    sizeBytes = (Get-Item -LiteralPath $package).Length
}
$json = $release | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $repo 'bimwiz-release.json'), $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Built $executable"
Write-Output "Prepared release manifest for BIMWiz $($release.version)."

[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts/publish')) + [IO.Path]::DirectorySeparatorChar
$source = (Get-Item -LiteralPath $PublishDirectory).FullName
if (-not $source.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Use a fresh build under .artifacts/publish.' }
foreach ($required in @('TaskAssist.exe', 'TaskAssist.dll', 'TaskAssist.runtimeconfig.json', 'outlook-worker/TaskAssist.OutlookWorker.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "Missing runtime file: $required" }
}
foreach ($item in @(Get-Item -LiteralPath $source) + @(Get-ChildItem -LiteralPath $source -Recurse -Force)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Links are not allowed in runtime output.' }
    if ($item.PSIsContainer) {
        if ($item.Name -match '^(?i:fixtures|tests|reports|Live|Demo|WindowTests|backups|exports)$') { throw 'Unexpected data directory in output.' }
    } elseif ($item.Extension -notin @('.exe','.dll','.json','.dat','.bin','.config','.xml','.txt','.md','.manifest')) {
        throw "Unexpected runtime file: $($item.Name)"
    }
    if ($item.Name -match '(?i)(\.(db|sqlite|sqlite3)(-|$)|\.pdb$|^profiles\.dat$|^preferences\.json$|^active-data\.txt$|^diagnostics\.json$)') { throw 'Private or debug output detected.' }
}
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$id = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$outputRoot = Join-Path $repositoryRoot ".artifacts/releases/$id"
$name = "TaskAssist-$version-win-x64"
$stage = Join-Path $outputRoot $name
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $stage -Recurse
foreach ($file in @('README.md','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $stage }
$guide = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $guide | Out-Null
foreach ($file in @('USER_GUIDE.md','OUTLOOK.md','IMPLEMENTATION_STATUS.md','RELEASE_NOTES.md','ICON.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs/$file") -Destination $guide
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses') -Destination $stage -Recurse
$hashes = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + $relative
})
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'),$hashes,[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $outputRoot "$name.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$archive,[IO.Compression.CompressionLevel]::Optimal,$true)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText($archive + '.sha256',"$hash  $name.zip`n",[Text.UTF8Encoding]::new($false))
Write-Host "Runtime folder: $stage"
Write-Host "Runtime archive: $archive"
Write-Host "SHA256: $hash"
Write-Host 'No application data was copied and no upload was performed. TaskAssist.exe requires its companion files.'

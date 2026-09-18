[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$prefix = $repositoryRoot + [IO.Path]::DirectorySeparatorChar
$manifest = Join-Path $repositoryRoot 'source-files.txt'
$entries = @(Get-Content -LiteralPath $manifest | Where-Object { $_.Trim() -ne '' })
if ($entries.Count -eq 0 -or @($entries | Sort-Object -Unique).Count -ne $entries.Count) {
    throw 'The source manifest is empty or contains duplicates.'
}
$files = @(foreach ($entry in $entries) {
    if ($entry -ne $entry.Trim() -or $entry -match '[\\:]' -or $entry.StartsWith('/') -or
        ($entry.Split('/') | Where-Object { $_ -in @('', '.', '..') })) {
        throw "Invalid relative path in source manifest: $entry"
    }
    if ($entry -match '(?i)(^|/)(\.git|bin|obj|\.artifacts|artifacts|reports|private|runtime-data|backups|exports|Live|Demo|WindowTests)(/|$)' -or
        $entry -match '(?i)(\.env($|\.)|\.(db|sqlite|sqlite3)(-|$)|\.(exe|dll|pdb|zip|lnk|log|msg|eml|pst|ost|pfx|p12|key|pem)$|(^|/)(profiles\.dat|preferences\.json|active-data\.txt|first-profile\.pending))') {
        throw "Private or generated path in source manifest: $entry"
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $entry))
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes the repository.' }
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer) { throw "Expected a file: $entry" }
    $cursor = $item
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Links are not allowed in source paths: $entry" }
        if ($cursor.FullName -eq $repositoryRoot) { break }
        if ($cursor -is [IO.FileInfo]) { $cursor = $cursor.Directory } else { $cursor = $cursor.Parent }
    }
    [pscustomobject]@{Relative=$entry; FullPath=$fullPath}
})
# Only explicit source entries enter the archive. Git metadata and build output are never copied.
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$outputDir = Join-Path $repositoryRoot '.artifacts/source'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$name = "TaskAssist-$version-source-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip'
$output = Join-Path $outputDir $name
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullPath, $file.Relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
[IO.File]::WriteAllText($output + '.sha256', "$hash  $name`n", [Text.UTF8Encoding]::new($false))
Write-Host "Source archive: $output"
Write-Host "Files: $($files.Count); SHA256: $hash"
Write-Host 'No upload was performed. Review all source content before publishing.'

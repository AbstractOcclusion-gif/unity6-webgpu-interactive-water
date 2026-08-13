$ErrorActionPreference = 'Stop'

$carriageReturnByte = [byte]13
$sourcePatterns = @(
    '*.cs',
    '*.shader',
    '*.compute',
    '*.hlsl',
    '*.cginc',
    '*.asmdef',
    '*.asmref',
    '*.ps1'
)

$stagedSourceFiles = @(
    git diff --cached --name-only --diff-filter=ACMR -- @sourcePatterns
)

if ($LASTEXITCODE -ne 0) {
    Write-Error 'Line-ending check could not enumerate staged source files.'
    exit 1
}

$invalidSourceFiles = [System.Collections.Generic.List[string]]::new()

foreach ($sourceFile in $stagedSourceFiles) {
    if (!(Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
        continue
    }

    $sourceBytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $sourceFile))
    if ($sourceBytes.Contains($carriageReturnByte)) {
        $invalidSourceFiles.Add($sourceFile)
    }
}

if ($invalidSourceFiles.Count -eq 0) {
    exit 0
}

Write-Host 'Line-ending check failed. These staged source files contain CRLF or mixed endings:'
foreach ($invalidSourceFile in $invalidSourceFiles) {
    Write-Host "  $invalidSourceFile"
}
Write-Host 'Convert them to LF, stage them again, and retry the commit.'
exit 1

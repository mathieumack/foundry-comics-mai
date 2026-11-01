param(
    [string[]] $Paths
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $Paths) {
    $Paths = @(git -C $repositoryRoot ls-files --cached --others --exclude-standard -- '*.md')
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list repository Markdown files.'
    }
}

$failures = [System.Collections.Generic.List[string]]::new()
$checkedLinks = 0

foreach ($path in $Paths) {
    $file = if ([System.IO.Path]::IsPathRooted($path)) { $path } else { Join-Path $repositoryRoot $path }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        $failures.Add("Markdown file does not exist: $path")
        continue
    }

    $content = Get-Content -LiteralPath $file -Raw
    $content = [regex]::Replace($content, '(?ms)^(`{3,}|~{3,})[^\r\n]*\r?\n.*?^\1[ \t]*\r?$', '')
    $links = [regex]::Matches($content, '!?\[[^\]\r\n]*\]\((?<target><[^>\r\n]+>|[^\s)]+)(?:\s+"[^"\r\n]*")?\)')

    foreach ($link in $links) {
        $target = $link.Groups['target'].Value.Trim('<', '>')
        if ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|//|#)') {
            continue
        }

        $relativePath = [System.Uri]::UnescapeDataString(($target -split '[?#]', 2)[0])
        if (-not $relativePath) {
            continue
        }

        $resolvedPath = Join-Path (Split-Path -Parent $file) $relativePath
        $checkedLinks++
        if (-not (Test-Path -LiteralPath $resolvedPath)) {
            $failures.Add("${path}: missing link target '$target'")
        }
    }
}

if ($failures.Count -gt 0) {
    throw ("Local Markdown link validation failed:`n" + ($failures -join "`n"))
}

Write-Output "Validated $checkedLinks local link targets in $($Paths.Count) Markdown files. External URLs and fragments were not checked."

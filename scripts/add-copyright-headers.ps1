#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Adds Microsoft copyright headers to all source files in the repository

.DESCRIPTION
    This script adds appropriate copyright headers to C#, TypeScript, Bicep,
    PowerShell, CSS, and Shell script files that don't already have them.
#>

param(
    [string]$RootPath = "."
)

$ErrorActionPreference = "Stop"

# Copyright header templates
$copyrightYear = (Get-Date).Year
$msftCopyright = @"
Copyright (c) Microsoft Corporation.
Licensed under the MIT License.

This solution is provided for testing, learning, and evaluation purposes only.
It is not intended for production use. Microsoft provides no support or guarantees.
Use at your own risk.
"@

# Template for C# files
$csharpHeader = @"
// $($msftCopyright -replace "`r`n", "`n// " -replace "`n", "`n// ")

"@

# Template for TypeScript/JavaScript files
$tsHeader = @"
// $($msftCopyright -replace "`r`n", "`n// " -replace "`n", "`n// ")

"@

# Template for Bicep files
$bicepHeader = @"
// $($msftCopyright -replace "`r`n", "`n// " -replace "`n", "`n// ")

"@

# Template for PowerShell files (excluding shebang)
$ps1Header = @"
# $($msftCopyright -replace "`r`n", "`n# " -replace "`n", "`n# ")

"@

# Template for Shell scripts (excluding shebang)
$shHeader = @"
# $($msftCopyright -replace "`r`n", "`n# " -replace "`n", "`n# ")

"@

# Template for CSS files
$cssHeader = @"
/*
 * $($msftCopyright -replace "`r`n", "`n * " -replace "`n", "`n * ")
 */

"@

function Add-CopyrightHeader {
    param(
        [string]$FilePath,
        [string]$Header,
        [bool]$HasShebang = $false
    )

    Write-Host "Processing: $FilePath"
    
    $content = Get-Content -Path $FilePath -Raw -Encoding UTF8
    
    # Check if file already has a copyright header
    if ($content -match "(Copyright|LICENSE|Licensed under)") {
        Write-Host "  Skipped (already has copyright/license)" -ForegroundColor Yellow
        return
    }

    if ($HasShebang -and $content -match "^#!") {
        # Extract shebang line and rest of content
        $lines = $content -split "`n", 2
        $shebang = $lines[0]
        $restOfContent = if ($lines.Count -gt 1) { $lines[1] } else { "" }
        
        # Add header after shebang
        $newContent = "$shebang`n$Header$restOfContent"
    }
    else {
        # Add header at the beginning
        $newContent = "$Header$content"
    }

    # Write back to file
    [System.IO.File]::WriteAllText($FilePath, $newContent, [System.Text.UTF8Encoding]::new($false))
    Write-Host "  Added copyright header" -ForegroundColor Green
}

# Process all files
$patterns = @{
    "*.cs" = @{ Header = $csharpHeader; HasShebang = $false }
    "*.ts" = @{ Header = $tsHeader; HasShebang = $false }
    "*.tsx" = @{ Header = $tsHeader; HasShebang = $false }
    "*.bicep" = @{ Header = $bicepHeader; HasShebang = $false }
    "*.ps1" = @{ Header = $ps1Header; HasShebang = $true }
    "*.sh" = @{ Header = $shHeader; HasShebang = $true }
    "*.css" = @{ Header = $cssHeader; HasShebang = $false }
}

$totalProcessed = 0
$totalAdded = 0

foreach ($pattern in $patterns.Keys) {
    $config = $patterns[$pattern]
    
    Write-Host "`nProcessing $pattern files..." -ForegroundColor Cyan
    
    $files = Get-ChildItem -Path $RootPath -Filter $pattern -Recurse -File | 
        Where-Object { 
            $_.FullName -notmatch "[\\/](node_modules|bin|obj|\.git)[\\/]" 
        }
    
    foreach ($file in $files) {
        $totalProcessed++
        Add-CopyrightHeader -FilePath $file.FullName -Header $config.Header -HasShebang $config.HasShebang
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Total files processed: $totalProcessed" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

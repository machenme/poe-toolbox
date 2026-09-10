param(
    [string]$InputDirectory = (Join-Path $PSScriptRoot "..\temp\dictionary"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\PoEToolbox.Core\Assets\Translation\base_item_terms.json")
)

$ErrorActionPreference = "Stop"

function Get-CleanName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    # PriceTagger appends values such as " [ 2.9e ]" to some client names.
    return ($Name -replace '\s+\[\s*\d+(?:\.\d+)?[cde]\s*\]$', '').Trim()
}

$sources = @(
    @{ Prefix = "BaseItemTypes"; English = "en.json"; Simplified = "zh-cn.json"; Traditional = "zh-tw.json"; AddArticlelessAlias = $false },
    @{ Prefix = "WorldAreas"; English = "en-map.json"; Simplified = "zh-cn-map.json"; Traditional = "zh-tw-map.json"; AddArticlelessAlias = $true }
)

$terms = foreach ($source in $sources) {
    $english = Get-Content -Raw (Join-Path $InputDirectory $source.English) | ConvertFrom-Json
    $simplified = Get-Content -Raw (Join-Path $InputDirectory $source.Simplified) | ConvertFrom-Json
    $traditional = Get-Content -Raw (Join-Path $InputDirectory $source.Traditional) | ConvertFrom-Json

    $simplifiedById = @{}
    $traditionalById = @{}
    foreach ($row in $simplified) { $simplifiedById[$row.Id] = Get-CleanName $row.Name }
    foreach ($row in $traditional) { $traditionalById[$row.Id] = Get-CleanName $row.Name }

    foreach ($row in $english) {
        $en = Get-CleanName $row.Name
        $cn = $simplifiedById[$row.Id]
        $tw = $traditionalById[$row.Id]
        if (-not ($en -and $cn -and $tw)) {
            continue
        }

        [ordered]@{
            Id = "$($source.Prefix):$($row.Id)"
            English = $en
            SimplifiedChinese = $cn
            TraditionalChinese = $tw
        }

        # Players often omit "The" when naming an area or boss in guides and chat.
        if ($source.AddArticlelessAlias -and $en -match '^The\s+(.+)$') {
            [ordered]@{
                Id = "$($source.Prefix):$($row.Id):articleless"
                English = $Matches[1]
                SimplifiedChinese = $cn
                TraditionalChinese = $tw
            }
        }
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$json = $terms | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($OutputPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $($terms.Count) game terms to $OutputPath"

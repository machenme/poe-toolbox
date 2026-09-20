param(
    [Parameter(Mandatory = $true)][string]$InputXml,
    [string]$OutputPath = ''
)

$ErrorActionPreference = "Stop"
if (-not $OutputPath) {
    $OutputPath = Join-Path $PSScriptRoot "..\src\PoEToolbox.Plugins.Poe2Font\Assets\official-uisettings-fonts.json"
}

# 从官方原始 uisettings.xml 抽取字体表，生成插件内嵌的「内置官方模板」。
# 用法：先用命令行只读解出 XML，再喂给本脚本。
#   PoEToolbox.Cli.exe extract-file <Bundles2\_.index.bin> metadata/ui/uisettings.xml C:\temp\uisettings.xml
#   powershell -File tools\BuildPoe2FontTemplate.ps1 -InputXml C:\temp\uisettings.xml

[xml]$document = Get-Content -Raw -LiteralPath $InputXml
$root = $document.DocumentElement

function Get-ScopeChain([Xml.XmlNode]$Node) {
    $chain = @()
    $current = $Node.ParentNode
    while ($null -ne $current -and $current.NodeType -eq 'Element') {
        if ($current.Name -eq 'Props') {
            $id = $current.GetAttribute('id')
            if ($id) { $chain = @($id) + $chain }
        }
        $current = $current.ParentNode
    }
    return , $chain
}

function Get-ScopePath([Xml.XmlNode]$Node) {
    return ((Get-ScopeChain $Node) -join '/')
}

$baseResolution = 2560
$sizeNode = $root.SelectSingleNode(".//Size[@id='BaseResolution']")
if ($null -ne $sizeNode) { [void][int]::TryParse($sizeNode.GetAttribute('width'), [ref]$baseResolution) }

$fontNodes = @($root.SelectNodes('.//Font'))
$byScope = @{}
foreach ($node in $fontNodes) {
    $scope = Get-ScopePath $node
    if (-not $byScope.ContainsKey($scope)) { $byScope[$scope] = @() }
    $byScope[$scope] += $node
}

function Get-FontAttribute([string[]]$Chain, [string]$Id, [string]$Attribute, [string[]]$Visited) {
    # 沿作用域链就近查找 id 为 $Id 的 Font，取 $Attribute；缺失时顺着 inherits 再解一层。
    for ($i = $Chain.Count - 1; $i -ge 0; $i = $i - 1) {
        $candidateScope = ($Chain[0..$i] -join '/')
        if (-not $byScope.ContainsKey($candidateScope)) { continue }
        $match = $byScope[$candidateScope] | Where-Object { $_.GetAttribute('id') -eq $Id } | Select-Object -First 1
        if ($null -eq $match) { continue }

        $direct = $match.GetAttribute($Attribute)
        if ($direct) { return $direct }

        $parent = $match.GetAttribute('inherits')
        $marker = "$candidateScope|$parent"
        if ($parent -and -not ($Visited -contains $marker)) {
            $subChain = @($Chain[0..$i])
            $inherited = Get-FontAttribute $subChain $parent $Attribute (@($Visited + $marker))
            if ($null -ne $inherited) { return $inherited }
        }
        return $null
    }
    return $null
}

$entries = foreach ($node in $fontNodes) {
    $chain = Get-ScopeChain $node
    $scope = $chain -join '/'
    $id = $node.GetAttribute('id')
    $typeface = $node.GetAttribute('typeface')
    $sizeRaw = $node.GetAttribute('size')
    $inherits = $node.GetAttribute('inherits')
    $explicitSize = 0
    $hasExplicitSize = [int]::TryParse($sizeRaw, [ref]$explicitSize)

    $effectiveTypeface = $typeface
    $effectiveSize = $explicitSize
    if ($inherits) {
        $visited = @("$scope|$id")
        if (-not $effectiveTypeface) { $effectiveTypeface = Get-FontAttribute $chain $inherits 'typeface' $visited }
        if (-not $hasExplicitSize) {
            $inheritedSize = Get-FontAttribute $chain $inherits 'size' $visited
            [void][int]::TryParse("$inheritedSize", [ref]$effectiveSize)
        }
    }

    $style = $node.GetAttribute('style')
    if ($node.GetAttribute('bold') -eq 'true') { $style = "$style bold" }
    if ($node.GetAttribute('italic') -eq 'true') { $style = "$style italic" }

    [pscustomobject]@{
        scope        = $scope
        id           = $id
        typeface     = $effectiveTypeface
        size         = $effectiveSize
        declaredSize = $sizeRaw
        inherits     = $inherits
        style        = $style.Trim()
    }
}

$fallbackFonts = foreach ($node in @($root.SelectNodes('.//FallbackFont'))) {
    [pscustomobject]@{
        id     = $node.GetAttribute('id')
        ranges = $node.GetAttribute('ranges')
        fonts  = $node.GetAttribute('fonts')
    }
}

$unresolved = @($entries | Where-Object { $_.size -le 0 } | ForEach-Object { "$($_.scope)/$($_.id)" })

$template = [pscustomobject]@{
    source          = 'metadata/ui/uisettings.xml (official, unmodified)'
    baseResolution  = $baseResolution
    generatedAt     = (Get-Date).ToString('yyyy-MM-dd')
    fonts           = @($entries | Sort-Object scope, id)
    fallbackFonts   = @($fallbackFonts)
}

$json = $template | ConvertTo-Json -Depth 6
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
# 不带 BOM：System.Text.Json 不跳 BOM，内嵌资源带就会在加载时抛异常。
[System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "fonts=$($entries.Count)  fallback=$($fallbackFonts.Count)  unresolvedSize=$($unresolved.Count)  -> $OutputPath"
if ($unresolved.Count -gt 0) { $unresolved | ForEach-Object { Write-Host "  未解析出字号: $_" } }

# Validates the CUI JSON that the plugin's harness writes.
# Checks anchor ranges, rect sanity and button commands, so layout mistakes are caught
# without a game client.
#
# CUI rules the checker models:
#   - A RectTransform with empty anchors belongs to a child that inherits its parent rect.
#   - Equal anchors mean the offsets carry the size, so offsets are what must not be inverted.
#   - Elements without a name are normal; CUI names most children automatically.
param(
    [string]$Directory = 'C:\RustServer\server\oxide\data\PrecisionSorter'
)

$ErrorActionPreference = 'Stop'

function Get-Pair([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    $parts = $value.Trim() -split '\s+'
    if ($parts.Count -ne 2) { return $null }
    $numbers = @()
    foreach ($part in $parts) {
        $parsed = 0.0
        if (-not [double]::TryParse($part, [ref]$parsed)) { return $null }
        $numbers += $parsed
    }
    return $numbers
}

$problems = New-Object System.Collections.Generic.List[string]

foreach ($file in @('ui-main.json', 'ui-picker.json')) {
    $path = Join-Path $Directory $file
    if (-not (Test-Path -LiteralPath $path)) {
        $problems.Add("$file : MISSING")
        continue
    }

    $elements = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $names = New-Object System.Collections.Generic.List[string]
    $anchored = 0
    $inherited = 0
    $buttons = 0
    $labels = 0

    foreach ($element in $elements) {
        if (-not [string]::IsNullOrWhiteSpace($element.name)) { $names.Add($element.name) }

        $name = if ($element.name) { $element.name } else { '<generated>' }
        $components = $element.components
        if (-not $components) {
            $problems.Add("$file/$name : no components")
            continue
        }

        foreach ($component in $components) {
            $type = [string]$component.type

            if ($type -match 'RectTransform') {
                $min = Get-Pair ([string]$component.anchormin)
                $max = Get-Pair ([string]$component.anchormax)

                if ($null -eq $min -and $null -eq $max) {
                    $inherited++
                    continue
                }

                $anchored++

                if ($null -eq $min -or $null -eq $max) {
                    $problems.Add("$file/$name : unparsable anchors '$($component.anchormin)' / '$($component.anchormax)'")
                    continue
                }

                foreach ($value in ($min + $max)) {
                    if ($value -lt 0 -or $value -gt 1) {
                        $problems.Add("$file/$name : anchor out of range ($value)")
                    }
                }

                $offsetMin = Get-Pair ([string]$component.offsetmin)
                $offsetMax = Get-Pair ([string]$component.offsetmax)
                $equalAnchors = ($min[0] -eq $max[0]) -and ($min[1] -eq $max[1])

                if ($equalAnchors) {
                    if ($null -eq $offsetMin -or $null -eq $offsetMax) {
                        $problems.Add("$file/$name : equal anchors with no offsets, so the rect has zero size")
                    }
                    elseif ($offsetMin[0] -ge $offsetMax[0] -or $offsetMin[1] -ge $offsetMax[1]) {
                        $problems.Add("$file/$name : inverted offsets min=$($component.offsetmin) max=$($component.offsetmax)")
                    }
                }
                elseif ($min[0] -ge $max[0] -or $min[1] -ge $max[1]) {
                    $problems.Add("$file/$name : inverted rect min=$($component.anchormin) max=$($component.anchormax)")
                }
            }

            if ($type -match 'Button') {
                $buttons++
                if ([string]::IsNullOrWhiteSpace([string]$component.command)) {
                    $problems.Add("$file/$name : button with no command")
                }
            }

            if ($type -match 'InputField') {
                if ([string]::IsNullOrWhiteSpace([string]$component.command)) {
                    $problems.Add("$file/$name : input field with no command")
                }
                if (-not $component.needsKeyboard) {
                    $problems.Add("$file/$name : input field without needsKeyboard, typing will not reach it")
                }
            }

            if ($type -match 'Text' -and -not ($type -match 'InputField')) { $labels++ }
        }
    }

    $duplicates = $names | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
    foreach ($duplicate in $duplicates) {
        $problems.Add("$file : duplicate element name '$duplicate'")
    }

    Write-Output "$file : elements=$($elements.Count) anchoredRects=$anchored inheritedRects=$inherited buttons=$buttons labels=$labels"
}

if ($problems.Count -eq 0) {
    Write-Output 'UI VALIDATION PASSED'
    exit 0
}

Write-Output "UI VALIDATION FAILED ($($problems.Count) problems)"
foreach ($problem in $problems) { Write-Output "  $problem" }
exit 1

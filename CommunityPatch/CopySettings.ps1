param(
    [Parameter(Mandatory = $true)][string]$SettingsFolder,
    [string]$WorkshopId
)
$ErrorActionPreference = 'Stop'
$settings = (Resolve-Path -LiteralPath $SettingsFolder).Path
if ($WorkshopId -and $WorkshopId -notmatch '^\d+$') { throw 'WorkshopId must be numeric.' }
$outputName = if ($WorkshopId) { "steam_$WorkshopId.json" } else { 'local_CommunityPatch.json' }
$destination = Join-Path $settings $outputName
$modules = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'modules.json') -Raw | ConvertFrom-Json).modules
$values = [ordered]@{}
foreach ($module in $modules) {
    if (!$module.settings.Count) { continue }
    $previous = Join-Path $settings ("local_{0}.json" -f $module.id)
    if (!(Test-Path -LiteralPath $previous -PathType Leaf)) { continue }
    $data = Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json
    foreach ($key in $module.settings) {
        $property = $data.PSObject.Properties[$key]
        if ($null -eq $property) { continue }
        if ($property.Value -isnot [bool]) { throw "Invalid saved setting $($module.id) / $key." }
        $values["$($module.id)__$key"] = $property.Value
    }
}
if ($values.Count -eq 0) {
    Write-Output 'No saved individual settings to copy. The pack uses the same defaults.'
    return
}
$json = $values | ConvertTo-Json -Depth 20
$file = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json + [Environment]::NewLine)
    $file.Write($bytes, 0, $bytes.Length)
} finally { $file.Dispose() }
Write-Output "Copied $($values.Count) setting(s) to $outputName."

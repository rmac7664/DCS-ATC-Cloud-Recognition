# Installs the dcsatc plugin into DCSServerBot (run on the server, as the user that runs the bot):
#   irm https://raw.githubusercontent.com/rmac7664/DCS-ATC-Cloud-Recognition/dcsserverbot/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$raw = 'https://raw.githubusercontent.com/rmac7664/DCS-ATC-Cloud-Recognition/dcsserverbot/plugins/dcsatc'
$main = Get-ChildItem "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Documents", 'C:\', 'D:\', 'E:\' -Recurse -Depth 4 -Filter main.yaml -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\config\main.yaml' -and (Test-Path (Join-Path $_.Directory.Parent.FullName 'plugins\admin')) } |
    Select-Object -First 1
if (-not $main) { throw 'DCSServerBot not found (config\main.yaml next to plugins\admin).' }
$bot = $main.Directory.Parent.FullName
$dir = Join-Path $bot 'plugins\dcsatc'
New-Item -ItemType Directory -Force $dir | Out-Null
foreach ($f in '__init__.py', 'version.py', 'commands.py') {
    Invoke-WebRequest "$raw/$f" -OutFile (Join-Path $dir $f) -UseBasicParsing
}
Write-Host "Plugin files: $dir"

$m = $main.FullName
$t = [IO.File]::ReadAllText($m)
if ($t -match '(?m)^\s*-\s*dcsatc\s*$' -or $t -match 'opt_plugins:.*\bdcsatc\b') {
    Write-Host 'main.yaml already lists dcsatc.'
} else {
    Copy-Item $m "$m.before-dcsatc"
    $nl = if ($t -match "`r`n") { "`r`n" } else { "`n" }
    if ($t -match '(?m)^opt_plugins:[ \t]*\r?\n([ \t]*)-') {
        $ind = $Matches[1]
        $t = ([regex]'(?m)^opt_plugins:[ \t]*\r?\n').Replace($t, "opt_plugins:$nl$ind- dcsatc$nl", 1)
    } elseif ($t -match '(?m)^opt_plugins:[ \t]*\[[ \t]*\]') {
        $t = ([regex]'(?m)^opt_plugins:[ \t]*\[[ \t]*\]').Replace($t, 'opt_plugins: [dcsatc]', 1)
    } elseif ($t -match '(?m)^opt_plugins:[ \t]*\[') {
        $t = ([regex]'(?m)^opt_plugins:[ \t]*\[').Replace($t, 'opt_plugins: [dcsatc, ', 1)
    } else {
        $t = $t.TrimEnd() + $nl + "opt_plugins:$nl  - dcsatc$nl"
    }
    [IO.File]::WriteAllText($m, $t)
    Write-Host "Added dcsatc to opt_plugins in $m (backup: main.yaml.before-dcsatc)."
}
Select-String $m -Pattern 'opt_plugins' -Context 0, 6 | ForEach-Object { $_.Context.PreContext; $_.Line; $_.Context.PostContext }
Write-Host ''
Write-Host 'Done. Now run /node restart in Discord, then /dcsatc status.' -ForegroundColor Green

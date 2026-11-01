<#
.SYNOPSIS
    Serve the screenshot worklist, so its "update from newest capture" buttons actually work.

.DESCRIPTION
    The reshoot loop this exists for:

      1. Launch ioSender yourself, once - your own config, or .\build.ps1 -DefaultConfig for the shots
         marked "default config". ONE launch for a whole session's worth of shots, not one per shot.
      2. Arrange a screen and capture it (Win+Shift+S). Snipping Tool auto-saves to the Screenshots
         folder; the worklist's top bar shows how old the newest capture is.
      3. Press "update from newest capture" on that shot's page - or just U. The capture is filed as
         docs\manual\img\<name>.png and the page swaps the image in place. No refresh, no rebuild.
      4. Next shot.

    A page opened straight off disk cannot write files, which is the whole reason this script exists:
    the buttons need something local that can. It serves docs\manual over loopback and answers two
    calls of its own - api/recent (what would be filed) and api/update (file it).

    Loopback only, and it binds http://localhost:<port>/ which needs no administrator rights.

.PARAMETER Port
    Default 8791.

.PARAMETER NoLaunch
    Serve without opening a browser.

.PARAMETER NoRegen
    Serve the worklist as it stands instead of regenerating it first. Use it when you have hand-edited
    the HTML; normally the regeneration is the point, since it re-reads index.html and the image files.

.EXAMPLE
    tools\serve-image-review.ps1
    Regenerate, serve, open the browser. Ctrl+C to stop.
#>
[CmdletBinding()]
param(
    [int]$Port = 8791,
    [switch]$NoLaunch,
    [switch]$NoRegen
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$docsDir = Join-Path $repoRoot 'docs\manual'
$imgDir = Join-Path $docsDir 'img'
$pagesFile = Join-Path $docsDir '_image-review-pages.html'
$screenshotsDir = 'C:\Users\steve\OneDrive\Pictures\Screenshots'

Add-Type -AssemblyName System.Drawing

if (-not $NoRegen) {
    & (Join-Path $PSScriptRoot 'gen-image-review.ps1') -Pages -NoLaunch
}
if (-not (Test-Path $pagesFile)) { throw "No worklist at $pagesFile - run tools\gen-image-review.ps1 -Pages" }

# --- helpers ---------------------------------------------------------------------------------------

function Get-RecentCaptures([int]$count = 10) {
    if (-not (Test-Path $screenshotsDir)) { return @() }
    @(Get-ChildItem -Path $screenshotsDir -Filter '*.png' -ErrorAction SilentlyContinue |
      Sort-Object LastWriteTime -Descending | Select-Object -First $count)
}

function Get-PixelSize([string]$path) {
    try {
        $img = [System.Drawing.Image]::FromFile($path)
        try { return "$($img.Width)x$($img.Height)" } finally { $img.Dispose() }
    } catch { return '' }
}

$mime = @{
    '.html' = 'text/html; charset=utf-8'; '.htm' = 'text/html; charset=utf-8'
    '.png' = 'image/png'; '.jpg' = 'image/jpeg'; '.jpeg' = 'image/jpeg'; '.gif' = 'image/gif'
    '.svg' = 'image/svg+xml'; '.css' = 'text/css'; '.js' = 'text/javascript'
    '.pdf' = 'application/pdf'; '.json' = 'application/json'
}

function Write-Reply($ctx, [int]$code, [string]$type, [byte[]]$body) {
    $ctx.Response.StatusCode = $code
    $ctx.Response.ContentType = $type
    # Every response here describes something that is about to change on disk, so nothing may be
    # cached - a cached PNG is precisely the bug the update button exists to avoid.
    $ctx.Response.Headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
    $ctx.Response.ContentLength64 = $body.Length
    $ctx.Response.OutputStream.Write($body, 0, $body.Length)
    $ctx.Response.OutputStream.Close()
}

function Write-Json($ctx, $obj, [int]$code = 200) {
    Write-Reply $ctx $code 'application/json; charset=utf-8' ([Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Compress -Depth 5)))
}

function Write-Text($ctx, [int]$code, [string]$text) {
    Write-Reply $ctx $code 'text/plain; charset=utf-8' ([Text.Encoding]::UTF8.GetBytes($text))
}

# --- the one call that changes anything --------------------------------------------------------------

function Invoke-Update([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return @{ ok = $false; error = 'no name given' } }

    # Refuse anything that is not a bare filename. This is loopback-only and single-user, but a path
    # arriving from a web page and being written to disk is the one place in this tool worth being
    # pedantic about: a '..' here would write outside the repo.
    if ($name -notmatch '^[A-Za-z0-9._-]+\.png$') { return @{ ok = $false; error = "bad name '$name'" } }

    $newest = Get-RecentCaptures 1 | Select-Object -First 1
    if (-not $newest) { return @{ ok = $false; error = "no captures in $screenshotsDir" } }

    $dest = Join-Path $imgDir $name
    try {
        Copy-Item -Path $newest.FullName -Destination $dest -Force
    } catch {
        return @{ ok = $false; error = "copy failed: $($_.Exception.Message)" }
    }

    $f = Get-Item $dest
    Write-Host ("==> {0} <- {1} ({2}, {3:N0} KB)" -f $name, $newest.Name, (Get-PixelSize $dest), ($f.Length / 1KB)) -ForegroundColor Green

    return @{
        ok     = $true
        name   = $name
        from   = $newest.Name
        taken  = $newest.LastWriteTime.ToString('o')
        mtime  = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        pixels = Get-PixelSize $dest
        bytes  = $f.Length
    }
}

# --- serve --------------------------------------------------------------------------------------------

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:$Port/")
try { $listener.Start() }
catch { throw "Could not listen on port $Port ($($_.Exception.Message)). Try -Port 8792." }

$url = "http://localhost:$Port/"
Write-Host "==> Serving $docsDir at $url" -ForegroundColor Cyan
Write-Host "==> Captures come from $screenshotsDir" -ForegroundColor Cyan
Write-Host "==> Ctrl+C to stop." -ForegroundColor Cyan
if (-not $NoLaunch) { Start-Process $url }

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $path = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath).TrimStart('/')

        try {
            if ($path -eq 'api/recent') {
                $recent = Get-RecentCaptures 10 | ForEach-Object {
                    @{ name = $_.Name; time = $_.LastWriteTime.ToString('o') }
                }
                Write-Json $ctx @{ ok = $true; newest = @($recent)[0]; recent = @($recent); folder = $screenshotsDir }
                continue
            }

            if ($path -eq 'api/update') {
                Write-Json $ctx (Invoke-Update $ctx.Request.QueryString['name'])
                continue
            }

            if ($path -eq '') { $path = '_image-review-pages.html' }

            # Serve only from docs\manual, resolved and re-checked - a served path is attacker-shaped
            # input even when the only client is this machine's own browser.
            $full = [IO.Path]::GetFullPath((Join-Path $docsDir $path))
            if (-not $full.StartsWith([IO.Path]::GetFullPath($docsDir), [StringComparison]::OrdinalIgnoreCase)) {
                Write-Text $ctx 403 'outside docs\manual'
                continue
            }
            if (-not (Test-Path $full -PathType Leaf)) {
                Write-Text $ctx 404 "not found: $path"
                continue
            }

            $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
            $type = if ($mime.ContainsKey($ext)) { $mime[$ext] } else { 'application/octet-stream' }
            Write-Reply $ctx 200 $type ([IO.File]::ReadAllBytes($full))
        }
        catch {
            # One bad request must not take the server down mid-session - say what happened and carry on.
            Write-Host "!! $path : $($_.Exception.Message)" -ForegroundColor Red
            try { Write-Text $ctx 500 $_.Exception.Message } catch { }
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
    Write-Host "==> Stopped." -ForegroundColor Cyan
}

<#
.SYNOPSIS
  Publish the ioSender V2 user manual (this folder) to the gh-pages branch of the V2 repo.

.DESCRIPTION
  The manual is a self-contained site (index.html + img/). This script copies the folder into a
  throwaway git repo, commits it as an orphan gh-pages branch, and force-pushes it to the target
  remote. gh-pages holds ONLY the manual (root of the branch), so the public Pages site is just the
  manual and none of the repo's other /docs content. Re-run after editing the manual to update the
  live site.

  Site URL (Pages must be enabled to serve gh-pages, path '/'):
      https://iosenderv2.github.io/ioSender/

.PARAMETER RemoteUrl
  Push target. Defaults to the 'v2' remote of the checkout this folder lives in.

.NOTES
  Auth: uses your git credential helper. In an automated shell where the helper isn't primed, set
  $env:GH_TOKEN first and the script will use it for the push only (never printed).
#>
param(
  [string]$RemoteUrl,
  [string]$Branch = "gh-pages"
)
# NB: keep ErrorActionPreference at Continue - git writes warnings/progress to stderr, which under
# 'Stop' PowerShell 5.1 turns into a terminating NativeCommandError. We check $LASTEXITCODE instead.
function Invoke-Git {
  & git $args
  if ($LASTEXITCODE -ne 0) { throw ("git " + ($args -join ' ') + " failed ($LASTEXITCODE)") }
}
$src = $PSScriptRoot

if (-not $RemoteUrl) {
  $RemoteUrl = (git -C $src remote get-url v2).Trim()
}
if (-not $RemoteUrl) { throw "No remote URL - pass -RemoteUrl or add a 'v2' remote." }

$tmp = Join-Path $env:TEMP ("ghpages_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
  Copy-Item (Join-Path $src "index.html") $tmp
  if (Test-Path (Join-Path $src "README.md")) { Copy-Item (Join-Path $src "README.md") $tmp }
  if (Test-Path (Join-Path $src "img"))       { Copy-Item (Join-Path $src "img") $tmp -Recurse }
  # Also publish the repo-root Overview.html (lineage + repo wiring + changelog) as overview.html.
  $overview = Join-Path $src "..\..\Overview.html"
  if (Test-Path $overview) { Copy-Item $overview (Join-Path $tmp "overview.html") }
  # .nojekyll: serve files verbatim (skip Jekyll, which can drop underscore-prefixed paths).
  New-Item -ItemType File -Path (Join-Path $tmp ".nojekyll") | Out-Null

  Push-Location $tmp
  try {
    Invoke-Git init -q
    Invoke-Git config core.autocrlf false
    Invoke-Git checkout -q -b $Branch
    Invoke-Git add -A
    Invoke-Git -c user.name="ioSender manual" -c user.email="noreply@iosender" commit -q -m "Publish ioSender V2 user manual"

    $pushUrl = $RemoteUrl
    if ($env:GH_TOKEN) {
      # Inject the token for this push only, without echoing it.
      $pushUrl = $RemoteUrl -replace '^https://', ("https://x-access-token:{0}@" -f $env:GH_TOKEN)
    }
    $env:GIT_TERMINAL_PROMPT = "0"
    Invoke-Git push -f $pushUrl ("{0}:{1}" -f $Branch, $Branch)
    Write-Host "Published to $Branch on $RemoteUrl" -ForegroundColor Green
    Write-Host "Site: https://iosenderv2.github.io/ioSender/"
  } finally { Pop-Location }
} finally {
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

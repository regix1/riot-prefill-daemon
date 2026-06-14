if (-Not(Get-Command git -ErrorAction SilentlyContinue))
{
    throw "This script requires git to be installed to continue!  Please install git and try again!"
}

Push-Location $PSScriptRoot
. ./SharedFunctions.ps1
Pop-Location

EnsureRmanIsDownloaded
CloneAndUpdateManifests

$manifestPath = DownloadManifest "LoL" "league-client"
&"$env:TEMP\RiotPrefill\bin\rman-dl.exe" $manifestPath --no-verify --no-write #--filter-lang en_US

$manifestPath = DownloadManifest "LoL" "lol-game-client"
&"$env:TEMP\RiotPrefill\bin\rman-dl.exe" $manifestPath --no-verify --no-write #--filter-lang en_US

$manifestPath = DownloadManifest "LoL" "lol-standalone-client-content"
&"$env:TEMP\RiotPrefill\bin\rman-dl.exe" $manifestPath --no-verify --no-write --filter-lang en_US


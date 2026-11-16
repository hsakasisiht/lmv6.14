# Powershell Live Game Version Resolver
$major = 2
$minor = 208
$build = 321

try {
    $apple = Invoke-RestMethod -Uri "https://itunes.apple.com/lookup?id=1071976327" -TimeoutSec 6 -Headers @{"User-Agent"="Mozilla/5.0"}
    if ($apple.results -and $apple.results.Count -gt 0) {
        $v = $apple.results[0].version
        if ($v -match '^(\d+)\.(\d+)') {
            $major = [int]$matches[1]
            $minor = [int]$matches[2]
        }
    }
} catch {}

try {
    $plist = (Invoke-WebRequest -Uri "http://download-lo-snd.igg.com/win6/Update.plist" -TimeoutSec 6 -UseBasicParsing -Headers @{"User-Agent"="Mozilla/5.0"}).Content
    $line1 = ($plist -split "`r?`n")[0]
    $parts = $line1 -split '/'
    if ($parts.Length -ge 3 -and $parts[2] -match '^\d+$') {
        $build = [int]$parts[2]
    }
} catch {}

Write-Output "$major.$minor.$build"

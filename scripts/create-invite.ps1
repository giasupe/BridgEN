param(
    [string]$UserId,
    [switch]$OpenFolder
)

$ErrorActionPreference = 'Stop'
$chooseUserId = [string]::IsNullOrWhiteSpace($UserId)

# Run this script on the administrator's own PC. The running Windows user's
# LocalApplicationData is authoritative; files in another execution environment
# are not evidence that the administrator has a copy of an invitation.
$localDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localDirectory)) {
    throw 'The current Windows user has no LocalApplicationData directory.'
}
$directory = Join-Path $localDirectory 'BridgEN\admin\invites'
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$registryPath = Join-Path $directory 'invitation-registry.json'
$utf8 = [System.Text.UTF8Encoding]::new($false)

# Serialize local issuances to avoid losing a user's registry entry.
$registryLock = [System.IO.File]::Open((Join-Path $directory 'invitation-registry.lock'),
    [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $registry = [ordered]@{}
    if ([System.IO.File]::Exists($registryPath)) {
        try {
            $savedRegistry = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($registryPath))
        } catch {
            throw 'The invitation registry is not valid JSON. Keep the existing file and repair it before issuing another invitation.'
        }
        if ($savedRegistry -isnot [System.Management.Automation.PSCustomObject]) {
            throw 'The invitation registry must be a JSON object.'
        }
        foreach ($entry in $savedRegistry.PSObject.Properties) {
            if ($entry.Name -cnotmatch '^[0-9a-f]{64}$' -or $entry.Value -isnot [string] -or
                $entry.Value -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$' -or
                $entry.Value -in @('constructor', 'prototype')) {
                throw 'The invitation registry contains an invalid hash or user ID.'
            }
            # OrderedDictionary is case insensitive, so compare IDs explicitly below.
            $registry[$entry.Name] = $entry.Value
        }
    }
    if ($registry.Count -ge 32) {
        throw 'This beta supports at most 32 invitation entries.'
    }

    if ($chooseUserId) {
        Write-Host '招待する人のUserIDを自由に決めてください。'
        Write-Host '半角英数字・ハイフン(-)・アンダースコア(_)で1～64文字。先頭は半角英数字です。'
        Write-Host '例: giasupe、alice、friend_01。中止する場合はCtrl+Cを押してください。'
    }
    while ($true) {
        if ($chooseUserId) { $UserId = Read-Host 'UserID' }
        $UserId = $UserId.Trim()
        $userIdError = $null
        if ($UserId -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') {
            $userIdError = 'UserIDは半角英数字から始まる1～64文字で入力してください。使える記号は - と _ です。'
        } elseif ($UserId -in @('constructor', 'prototype')) {
            $userIdError = 'そのUserIDは予約語のため使えません。別の名前を入力してください。'
        } elseif (@($registry.Values) -ccontains $UserId) {
            $userIdError = 'そのUserIDは登録済みです。別の名前を入力してください。既存のトークンは変更しません。'
        }
        if ($null -eq $userIdError) { break }
        if (-not $chooseUserId) { throw $userIdError }
        Write-Host $userIdError -ForegroundColor Yellow
    }

    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = New-Object byte[] 32
        $random.GetBytes($bytes)
        $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $hash = [BitConverter]::ToString($sha256.ComputeHash($utf8.GetBytes($token))).Replace('-', '').ToLowerInvariant()
    } finally {
        $random.Dispose()
        $sha256.Dispose()
    }
    $registry[$hash] = $UserId
    $registration = ConvertTo-Json -InputObject $registry
    if ($utf8.GetByteCount($registration) -gt 5000) {
        throw 'Invitation registry exceeds the Secret size budget.'
    }

    $stamp = [Guid]::NewGuid().ToString('N')
    $tokenPath = Join-Path $directory "$UserId-$stamp.token.txt"
    $registrationPath = Join-Path $directory "$UserId-$stamp.registration.json"
    [System.IO.File]::WriteAllText($tokenPath, $token, $utf8)
    [System.IO.File]::WriteAllText($registrationPath,
        (ConvertTo-Json -InputObject ([ordered]@{ $hash = $UserId })), $utf8)
    $temporaryRegistryPath = Join-Path $directory "registry-$stamp.tmp"
    [System.IO.File]::WriteAllText($temporaryRegistryPath, $registration, $utf8)
    if ([System.IO.File]::Exists($registryPath)) {
        # Windows PowerShell can bind $null to an empty string for a string
        # parameter. Give Replace a real backup path so the registry update
        # succeeds and the previous registrations remain recoverable.
        $registryBackupPath = Join-Path $directory "invitation-registry-$stamp.backup.json"
        [System.IO.File]::Replace($temporaryRegistryPath, $registryPath, $registryBackupPath)
    } else {
        [System.IO.File]::Move($temporaryRegistryPath, $registryPath)
    }
} finally {
    $registryLock.Dispose()
}

# Print paths only. Never put a raw token in a terminal transcript or chat.
Write-Output "作成したUserID: $UserId"
Write-Output "Invite token file (give only to this user): $tokenPath"
Write-Output "Single user registration JSON: $registrationPath"
Write-Output "Set Cloudflare INVITE_TOKENS_JSON to the combined registry: $registryPath"
if ($OpenFolder) {
    Invoke-Item -LiteralPath $directory
}

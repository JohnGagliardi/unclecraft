# Makes the UncleCraft release-signing key. Run once, on your own PC, signed in to gh (gh auth status):
#   powershell -ExecutionPolicy Bypass -File tools\new-signing-key.ps1
#   pwsh -File tools\new-signing-key.ps1 [-BackupPath E:\unclecraft-signing-key.pem]
# Works on Windows PowerShell 5.1 and PowerShell 7. -BackupPath needs PowerShell 7 (5.1's .NET has no
# encrypted PKCS#8 export); on 5.1 it stops before doing anything.
# What it does:
#   1. Makes a new ECDSA P-256 key in memory (on 5.1: an ephemeral CNG key, never persisted).
#   2. Pipes the private key (PKCS#8 PEM) straight into: gh secret set UNCLECRAFT_SIGNING_KEY --repo JohnGagliardi/unclecraft
#      It is never written to disk or shown.
#   3. Writes the public key to UpdateKey.pub.txt (app root). Commit that file: the app checks releases against it.
#   4. With -BackupPath (PowerShell 7 only), also saves a passphrase-encrypted copy of the private key (e.g. on a
#      USB stick kept off this PC). Without a backup, a lost key means a new key and every family reinstalling once.
# Running it again replaces the key: builds with the old public key then reject new releases until reinstalled.
# Note: the current key was made with a Node fallback (crypto.generateKeyPairSync('ec', namedCurve P-256),
# PKCS#8/SPKI PEM) piped the same way; any tool that makes a P-256 PKCS#8 + SPKI PEM pair works.
# -DryRun -PrivOut x.pem -PubOut y.pem: test only. Writes both PEMs to those paths; no gh, no UpdateKey.pub.txt.
param([string]$BackupPath, [switch]$DryRun, [string]$PrivOut, [string]$PubOut)
$ErrorActionPreference = 'Stop'
$repo = 'JohnGagliardi/unclecraft'
$pubFile = Join-Path $PSScriptRoot '..\UpdateKey.pub.txt'
$isPs7 = $PSVersionTable.PSVersion.Major -ge 7

if ($BackupPath -and -not $isPs7) {
    throw 'The encrypted backup (-BackupPath) needs PowerShell 7 (pwsh). Run this with pwsh for a backup, or run again without -BackupPath to skip it. Nothing was changed.'
}
if ($DryRun -and (-not $PrivOut -or -not $PubOut)) { throw '-DryRun needs -PrivOut and -PubOut' }

function ConvertTo-Pem([byte[]]$der, [string]$label) {
    $b64 = [Convert]::ToBase64String($der)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("-----BEGIN $label-----`n")
    for ($i = 0; $i -lt $b64.Length; $i += 64) {
        [void]$sb.Append($b64.Substring($i, [Math]::Min(64, $b64.Length - $i))).Append("`n")
    }
    [void]$sb.Append("-----END $label-----")
    $s = $sb.ToString()
    $b64 = $null; [void]$sb.Clear()
    return $s
}

# Feeds $text to gh's stdin as raw ASCII bytes: no BOM, no newline rewriting, same on 5.1 and 7.
function Set-GhSecret([string]$text) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'gh'
    $psi.Arguments = "secret set UNCLECRAFT_SIGNING_KEY --repo $repo"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($text)
    try {
        $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $p.StandardInput.BaseStream.Flush()
        $p.StandardInput.Close()
        $p.WaitForExit()
        if ($p.ExitCode) { throw 'gh secret set failed; nothing else was changed' }
    } finally { [Array]::Clear($bytes, 0, $bytes.Length); $p.Dispose() }
}

if (-not $DryRun) {
    gh auth status *> $null
    if ($LASTEXITCODE) { throw 'Sign in to GitHub first: gh auth login' }
}

$key = $null; $priv = $null; $pub = $null
$pkcs8 = $null; $blob = $null; $spki = $null
try {
    if ($isPs7) {
        $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
        $priv = $key.ExportPkcs8PrivateKeyPem()
        $pub = $key.ExportSubjectPublicKeyInfoPem()
    } else {
        $cp = New-Object System.Security.Cryptography.CngKeyCreationParameters
        $cp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
        # [NullString]::Value: a plain $null would become "" and ask for a named (persisted) key.
        $key = [System.Security.Cryptography.CngKey]::Create(
            [System.Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $cp)
        $pkcs8 = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
        $priv = ConvertTo-Pem $pkcs8 'PRIVATE KEY'
        # EccPublicBlob = 'ECS1' + cbKey(32, LE) + X(32) + Y(32). SPKI = fixed P-256 header + 04 || X || Y.
        $blob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        if ($blob.Length -ne 72 -or [BitConverter]::ToInt32($blob, 4) -ne 32) { throw 'Unexpected EccPublicBlob layout' }
        [byte[]]$hdr = 0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,
                       0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00,0x04
        $spki = New-Object byte[] ($hdr.Length + 64)
        [Array]::Copy($hdr, 0, $spki, 0, $hdr.Length)
        [Array]::Copy($blob, 8, $spki, $hdr.Length, 64)
        $pub = ConvertTo-Pem $spki 'PUBLIC KEY'
    }

    if ($DryRun) {
        [System.IO.File]::WriteAllText($PrivOut, $priv + "`n")
        [System.IO.File]::WriteAllText($PubOut, $pub + "`n")
        $priv = $null
        "Dry run: PEMs written to $PrivOut and $PubOut (gh and UpdateKey.pub.txt untouched)."
        return
    }

    Set-GhSecret ($priv + "`n")
    $priv = $null

    if ($BackupPath) {
        $pass = Read-Host -AsSecureString 'Backup passphrase (keep it somewhere safe)'
        $plain = [System.Net.NetworkCredential]::new('', $pass).Password
        try {
            $pbe = [System.Security.Cryptography.PbeParameters]::new(
                [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
            [System.IO.File]::WriteAllText($BackupPath, $key.ExportEncryptedPkcs8PrivateKeyPem($plain, $pbe))
        } finally { $plain = $null; $pass.Dispose() }
        "Encrypted backup written: $BackupPath"
    }

    [System.IO.File]::WriteAllText($pubFile, $pub + "`n")
    "Secret UNCLECRAFT_SIGNING_KEY set on $repo."
    "Public key written: $((Resolve-Path $pubFile).Path) -- commit it to turn updates on."
} finally {
    foreach ($a in @($pkcs8, $blob, $spki)) { if ($a) { [Array]::Clear($a, 0, $a.Length) } }
    $priv = $null
    if ($key) { $key.Dispose() }
    [GC]::Collect()
}

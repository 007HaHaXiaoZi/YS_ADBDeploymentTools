$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\.build\github-test-' + [Guid]::NewGuid().ToString('N'))))
$source = Join-Path $root 'client'
$null = New-Item -ItemType Directory -Path (Join-Path $source 'tools') -Force
[IO.File]::WriteAllText((Join-Path $source 'YS_ADBDeploymentTools.exe'), 'fake executable for package validation only')
foreach ($name in @('Unity_QC.apk', 'Unity_DL.apk', 'CameraEffect.json', 'libdsp_wrapper.so')) {
    [IO.File]::WriteAllText((Join-Path $source "tools\$name"), '{}')
}
[IO.File]::WriteAllText((Join-Path $source 'publisher-private.pem'), 'must not be uploaded')
$publisher = Join-Path $PSScriptRoot '..\YS_ADBReleaseTool.exe'
$keys = Join-Path $root 'keys'
& $publisher keygen $keys
if ($LASTEXITCODE -ne 0) { throw 'Test keygen failed' }
$output = Join-Path $root 'assets'
& $publisher publish-github $source $output 'example/como-updates' '2.0.1' (Join-Path $keys 'publisher-private.pem') 'local test only'
$bytes = [IO.File]::ReadAllBytes((Join-Path $output 'manifest.json'))
$sig = [IO.File]::ReadAllBytes((Join-Path $output 'manifest.json.sig'))
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $keys 'publisher-public.pem')))
    if (!$rsa.VerifyData($bytes, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Invalid GitHub manifest signature' }
} finally { $rsa.Dispose() }
$manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
if ($manifest.Files.Count -ne 1) { throw 'Only the EXE may be published; tools must remain local' }
foreach ($file in $manifest.Files) {
    $uri = [Uri]$file.Url
    if (!$file.Url.StartsWith('https://github.com/example/como-updates/releases/download/v2.0.1/')) { throw 'Wrong release URL' }
    $asset = $uri.Segments[-1]
    if ($asset -notmatch '^YS_ADBDeploymentTools\.exe$') { throw 'GitHub asset name is not portable ASCII' }
    $path = Join-Path $output $asset
    if ((Get-Item -LiteralPath $path).Length -ne $file.Size) { throw 'Asset size mismatch' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'Asset hash mismatch' }
}
if ((Get-ChildItem -LiteralPath $output -File).Count -ne 3) { throw 'Unexpected file leaked into release assets' }
Write-Output 'PASS: GitHub signature, asset URL/hash; tools and private keys excluded. No upload performed.'

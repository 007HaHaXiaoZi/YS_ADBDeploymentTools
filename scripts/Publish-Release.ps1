param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Notes = '更新 YS_ADBDeploymentTools',
    [string]$Repository = '007HaHaXiaoZi/YS_ADBDeploymentTools',
    [string]$PrivateKey,
    [string]$OutputDirectory,
    [switch]$Upload
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseVersion = [Version]::Parse($Version).ToString()
if (!$PrivateKey) { $PrivateKey = Join-Path $root '.release-private\publisher-private.pem' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ".build\releases\$releaseVersion" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $root 'YS_ADBReleaseTool.exe') publish-github $root $output $Repository $releaseVersion $PrivateKey $Notes
if ($LASTEXITCODE -ne 0) { throw '生成签名更新包失败。' }
if (!$Upload) { Write-Output "已生成本地文件：$output；加 -Upload 才会上传。"; return }

# 只从当前用户的 Git Credential Manager 取 GitHub 凭据；不显示、不落盘令牌。
$credentialInput = "protocol=https`nhost=github.com`n`n"
$credentialOutput = $credentialInput | & git -c credential.interactive=never credential fill
if ($LASTEXITCODE -ne 0) { throw '没有可用的 HTTPS 凭据。请先运行 git credential-manager github login。' }
$passwordLine = $credentialOutput | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
if (!$passwordLine) { throw 'GitHub 凭据中没有令牌。' }
$headers = @{ Authorization = 'Bearer ' + $passwordLine.Substring(9); Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$api = "https://api.github.com/repos/$Repository"
$repoInfo = Invoke-RestMethod -Uri $api -Headers $headers
if ($repoInfo.private) { throw '匿名客户端无法读取私有仓库 Release；请使用公开仓库或带鉴权的更新服务。' }
$tag = "v$releaseVersion"
$existing = @(Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers) | Where-Object { $_.tag_name -eq $tag }
if ($existing) { throw "Release $tag 已存在，拒绝覆盖。请检查已有发布，或使用新版本。" }
$body = @{ tag_name=$tag; target_commitish=$repoInfo.default_branch; name=$tag; body=$Notes; draft=$true; prerelease=$false } | ConvertTo-Json
$release = Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json'
Write-Output "已创建草稿 Release：$tag"
foreach ($file in (Get-ChildItem -LiteralPath $output -File)) {
    if ($file.Name -notmatch '^(manifest\.json(\.sig)?|YS_ADBDeploymentTools\.exe)$') { throw "拒绝上传非预期附件：$($file.Name)" }
    $uploadBase = ($release.upload_url -split '\{')[0]
    if (([Uri]$uploadBase).Host -ne 'uploads.github.com') { throw 'GitHub 返回了意外的上传地址。' }
    $null = Invoke-RestMethod -Uri ($uploadBase + '?name=' + [Uri]::EscapeDataString($file.Name)) -Method Post -Headers $headers -InFile $file.FullName -ContentType 'application/octet-stream'
    Write-Output "已上传：$($file.Name)"
}
$published = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output "发布成功：$($published.html_url)"
Write-Output "客户端检查地址：https://github.com/$Repository/releases/latest/download/manifest.json"

param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$Notes = '更新 YS_ADBDeploymentTools',
    [string]$Repository = '007HaHaXiaoZi/YS_ADBDeploymentTools',
    [string]$PrivateKey,
    [string]$OutputDirectory,
    [switch]$Upload,
    [switch]$Push
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$arguments = @('release', $Version, '--root', $root, '--notes', $Notes, '--repository', $Repository)
if ($PrivateKey) { $arguments += @('--key', $PrivateKey) }
if ($OutputDirectory) { $arguments += @('--output', $OutputDirectory) }
if ($Upload) { $arguments += '--upload' }
if ($Push) { $arguments += '--push' }
& (Join-Path $root 'YS_ADBReleaseTool.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw '发布工具执行失败。请查看上方错误。' }
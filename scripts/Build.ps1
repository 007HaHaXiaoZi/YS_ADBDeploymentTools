param([switch]$Offline, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { 'C:\Program Files\dotnet\dotnet.exe' }
if (!(Test-Path -LiteralPath $dotnet)) { throw '请先安装 .NET 9 SDK。' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$restoreArgs = @()
if ($Offline) {
    $config = Join-Path $root '.build\NuGet.offline.config'
    $null = [IO.Directory]::CreateDirectory((Split-Path $config))
    [IO.File]::WriteAllText($config,'<configuration><packageSources><clear /></packageSources></configuration>')
    $restoreArgs = @('--configfile', $config)
}
Push-Location $root
try {
    if (!$SkipTests) {
        & $dotnet build '.\tests\DeploymentTool.Tests\DeploymentTool.Tests.csproj' -c Release @restoreArgs
        if ($LASTEXITCODE -ne 0) { throw '测试构建失败。' }
        & '.\tests\DeploymentTool.Tests\bin\Release\net9.0-windows\win-x64\DeploymentTool.Tests.exe'
        if ($LASTEXITCODE -ne 0) { throw '测试未通过。' }
    }
    & $dotnet publish '.\src\AdbDeploymentTool\AdbDeploymentTool.csproj' -c Release -o '.\.build\publish\client' @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw '客户端构建失败。' }
    & $dotnet publish '.\src\ReleaseTool\ReleaseTool.csproj' -c Release -o '.\.build\publish\publisher' @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw '发布工具构建失败。' }
    Copy-Item -LiteralPath '.\.build\publish\client\YS_ADBDeploymentTools.exe' -Destination '.\YS_ADBDeploymentTools.exe' -Force
    Copy-Item -LiteralPath '.\.build\publish\publisher\YS_ADBReleaseTool.exe' -Destination '.\YS_ADBReleaseTool.exe' -Force
    Write-Output '构建完成：YS_ADBDeploymentTools.exe 和 YS_ADBReleaseTool.exe。tools 未参与打包。'
} finally { Pop-Location }

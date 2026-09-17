param([string]$ClientExe = "$PSScriptRoot\..\YS_ADBDeploymentTools.exe",
      [string]$PublisherExe = "$PSScriptRoot\..\YS_ADBReleaseTool.exe")
$ErrorActionPreference = 'Stop'
$clientVersion = [Version]::Parse((Get-Item -LiteralPath $ClientExe).VersionInfo.FileVersion)
$testVersion = [Version]::new($clientVersion.Major, $clientVersion.Minor, [Math]::Max(0, $clientVersion.Build) + 1).ToString()
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ("..\.build\packaged-test-" + [Guid]::NewGuid().ToString('N'))))
$client = Join-Path $testRoot 'client'
$source = Join-Path $testRoot 'source'
$stage = Join-Path $client '.updates\stage-test'
$runner = Join-Path $client '.updates\runner-test\UpdateRunner.exe'
foreach ($dir in @($client, $source, $stage, (Split-Path $runner))) { $null = New-Item -ItemType Directory -Path $dir -Force }
Copy-Item -LiteralPath $ClientExe -Destination (Join-Path $client 'YS_ADBDeploymentTools.exe')
Copy-Item -LiteralPath $ClientExe -Destination $runner
$probeSource = @'
using System;
using System.IO;
public static class Probe {
 public static void Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "restart-probe.txt"), "restarted"); }
}
'@
$probePath = Join-Path $testRoot 'Probe.cs'
[IO.File]::WriteAllText($probePath, $probeSource)
$probeExe = Join-Path $source 'YS_ADBDeploymentTools.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe "/out:$probeExe" $probePath
if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed' }
$keys = Join-Path $testRoot 'keys'
& $PublisherExe keygen $keys
if ($LASTEXITCODE -ne 0) { throw 'Key generation failed' }
$published = Join-Path $testRoot 'published'
& $PublisherExe publish $source $published 'https://updates.example.test/' $testVersion (Join-Path $keys 'publisher-private.pem') 'packaged updater smoke test'
if ($LASTEXITCODE -ne 0) { throw 'Signed publishing failed' }
$settings = @{ AutoCheck = $false; ManifestUrl = 'https://updates.example.test/manifest.json'; PublicKeyPem = [IO.File]::ReadAllText((Join-Path $keys 'publisher-public.pem')) }
[IO.File]::WriteAllText((Join-Path $client 'update-settings.json'), ($settings | ConvertTo-Json))
Copy-Item -LiteralPath (Join-Path $published 'manifest.json') -Destination $stage
Copy-Item -LiteralPath (Join-Path $published 'manifest.json.sig') -Destination $stage
Copy-Item -LiteralPath (Join-Path $published "files\$testVersion\YS_ADBDeploymentTools.exe") -Destination $stage

# 传入已不存在的进程 ID，模拟主程序已退出；更新器启动的替代客户端只写标记，不显示窗口，不连接设备。
$start = [Diagnostics.ProcessStartInfo]::new($runner)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($arg in @('--apply-update', $client, $stage, '2147483647', '0')) { $start.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::Start($start)
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Packaged updater timed out' }
if ($process.ExitCode -ne 0) { throw "Packaged updater failed: $($process.ExitCode)" }
$watch = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath (Join-Path $client 'restart-probe.txt')) -and $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
if (!(Test-Path -LiteralPath (Join-Path $client 'restart-probe.txt'))) { throw 'Updated client did not restart' }
if ([IO.File]::ReadAllText((Join-Path $client 'installed-version.txt')).Trim() -ne $testVersion) { throw 'Installed version did not advance' }
if ((Get-FileHash -LiteralPath (Join-Path $client 'YS_ADBDeploymentTools.exe')).Hash -ne (Get-FileHash -LiteralPath $probeExe).Hash) { throw 'Packaged replacement hash mismatch' }
Write-Output 'PASS: packaged EXE updater verified signature, replaced the executable, recorded version, and restarted the new client.'
Write-Output "Test artifacts: $testRoot (test-only signing key, do not distribute)"

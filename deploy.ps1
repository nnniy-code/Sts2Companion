#requires -Version 5.1
<#
.SYNOPSIS
    一键编译并部署 Sts2Companion.dll 到 Slay the Spire 2 的 mods 目录。

.DESCRIPTION
    默认行为：
      1. 自动找到本脚本旁边的 Sts2Companion.csproj（也兼容 D:\sts2_companion\mod\Sts2Companion 布局）；
      2. dotnet build -c Release；
      3. 和游戏 mods 目录里的 DLL 比哈希：一样就跳过，不一样才备份 + 覆盖；
      4. 校验哈希，打印结果。
    游戏正在运行时（有线程/内存的活进程）默认拒绝覆盖；-Kill 会先结束游戏，-Force 会硬写。
    残留的 0 线程/0 内存进程条目不会被当成活进程。

.PARAMETER Force
    跳过"游戏仍在运行"检查，直接尝试覆盖（文件被锁时仍会失败）。

.PARAMETER Kill
    发现活进程时先强制结束游戏，再部署。有未保存进度丢失风险。

.PARAMETER SkipBuild
    不编译，直接部署已有的 bin\Release\Sts2Companion.dll。

.PARAMETER Launch
    部署完成后启动游戏（游戏已经在跑时不重复启动）。

.PARAMETER DryRun
    只打印解析出的路径和检查结果，不编译、不写文件。

.PARAMETER ProjectDir
    C# 工程目录，默认自动探测脚本所在目录。

.PARAMETER GameDir
    游戏程序集目录（含 sts2.dll），默认 D:\steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64。
    也可以传游戏根目录，脚本会自动补 data_sts2_windows_x86_64。

.PARAMETER ModsDir
    mod 目录，默认 <游戏根>\mods\Sts2Companion。

.EXAMPLE
    .\deploy.ps1

.EXAMPLE
    .\deploy.ps1 -Kill -Launch

.EXAMPLE
    .\deploy.ps1 -DryRun

.EXAMPLE
    .\deploy.ps1 -ModsDir .\_deploytest -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Kill,
    [switch]$SkipBuild,
    [switch]$Launch,
    [switch]$DryRun,
    [string]$ProjectDir,
    [string]$GameDir,
    [string]$ModsDir
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# ---------- 1. 解析路径 ----------
$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = (Get-Location).Path
}

if ([string]::IsNullOrWhiteSpace($ProjectDir)) {
    $projectCandidates = @(
        (Join-Path $root 'Sts2Companion.csproj'),
        (Join-Path $root 'mod\Sts2Companion\Sts2Companion.csproj')
    )
    foreach ($candidate in $projectCandidates) {
        if (Test-Path -LiteralPath $candidate) {
            $ProjectDir = Split-Path -Parent $candidate
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($ProjectDir)) {
        $ProjectDir = 'D:\sts2_companion\mod\Sts2Companion'
    }
}

if ($ProjectDir -like '*.csproj') {
    $csproj = $ProjectDir
    $ProjectDir = Split-Path -Parent $ProjectDir
}
else {
    $csproj = Join-Path $ProjectDir 'Sts2Companion.csproj'
}

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = 'D:\steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64'
}

# 允许传游戏根目录
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'sts2.dll'))) {
    $nested = Join-Path $GameDir 'data_sts2_windows_x86_64'
    if (Test-Path -LiteralPath (Join-Path $nested 'sts2.dll')) {
        $GameDir = $nested
    }
}

if ([string]::IsNullOrWhiteSpace($ModsDir)) {
    if ((Split-Path -Leaf $GameDir) -like 'data_sts2*') {
        $ModsDir = Join-Path (Split-Path -Parent $GameDir) 'mods\Sts2Companion'
    }
    else {
        $ModsDir = Join-Path $GameDir 'mods\Sts2Companion'
    }
}

$builtDll = Join-Path $ProjectDir 'bin\Release\Sts2Companion.dll'
$sourceManifest = Join-Path $ProjectDir 'Sts2Companion.json'
$targetDll = Join-Path $ModsDir 'Sts2Companion.dll'
$targetManifest = Join-Path $ModsDir 'Sts2Companion.json'

Write-Host "Sts2Companion 部署" -ForegroundColor White
Write-Host "  工程   : $csproj"
Write-Host "  游戏   : $GameDir"
Write-Host "  mod 目录: $ModsDir"
Write-Host "  产物   : $builtDll"

if (-not (Test-Path -LiteralPath $csproj)) {
    throw "找不到工程文件: $csproj（用 -ProjectDir 指定）"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'sts2.dll'))) {
    throw "找不到游戏程序集: $(Join-Path $GameDir 'sts2.dll')（用 -GameDir 指定游戏目录）"
}

if ($DryRun) {
    Write-Step 'DryRun：只检查，不编译不写盘'
    $targetExists = Test-Path -LiteralPath $targetDll
    Write-Host ("  目标 DLL : {0} ({1})" -f $targetDll, $(if ($targetExists) { '已存在' } else { '不存在' }))
    Write-Host ("  目标 JSON: {0} ({1})" -f $targetManifest, $(if (Test-Path -LiteralPath $targetManifest) { '已存在' } else { '不存在' }))
    if ($targetExists -and (Test-Path -LiteralPath $builtDll)) {
        $same = (Get-FileHash -LiteralPath $targetDll).Hash -eq (Get-FileHash -LiteralPath $builtDll).Hash
        Write-Host ("  当前 DLL 与已编译产物: {0}" -f $(if ($same) { '一致，无需覆盖' } else { '不一致，会覆盖' }))
    }
    Write-Host "  DryRun 完成。" -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $ModsDir)) {
    New-Item -ItemType Directory -Path $ModsDir -Force | Out-Null
    Write-Host "  已创建 mod 目录: $ModsDir"
}

# ---------- 2. 编译 ----------
if (-not $SkipBuild) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "找不到 dotnet。请安装 .NET SDK，或把 dotnet 加进 PATH。"
    }

    Write-Step "编译 $csproj"
    $buildOutput = & $dotnet.Source build $csproj -c Release --nologo -v m 2>&1
    if ($LASTEXITCODE -ne 0) {
        $buildOutput | ForEach-Object { Write-Host $_ }
        throw "编译失败（exit $LASTEXITCODE）"
    }
    $buildOutput | Select-Object -Last 3 | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
}
else {
    Write-Step '跳过编译（-SkipBuild）'
}

if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "编译产物不存在: $builtDll"
}

# ---------- 3. 哈希比较：没变化就不折腾 ----------
$dllChanged = $true
if (Test-Path -LiteralPath $targetDll) {
    $dllChanged = (Get-FileHash -LiteralPath $targetDll).Hash -ne (Get-FileHash -LiteralPath $builtDll).Hash
}

$manifestChanged = $false
if (Test-Path -LiteralPath $sourceManifest) {
    if (Test-Path -LiteralPath $targetManifest) {
        $manifestChanged = (Get-FileHash -LiteralPath $sourceManifest).Hash -ne (Get-FileHash -LiteralPath $targetManifest).Hash
    }
    else {
        $manifestChanged = $true
    }
}

if (-not $dllChanged -and -not $manifestChanged) {
    Write-Step '已是最新：目标 DLL/JSON 与编译产物哈希一致，无需覆盖'
    $info = Get-Item -LiteralPath $targetDll
    Write-Host ("  当前版本: {0} 字节  {1}" -f $info.Length, $info.LastWriteTime) -ForegroundColor Green
}
else {
    # ---------- 4. 区分活进程和残留条目 ----------
    $live = @()
    $zombie = @()
    foreach ($p in @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)) {
        $threads = 0
        $mem = 0
        try { $threads = $p.Threads.Count } catch { }
        try { $mem = $p.WorkingSet64 } catch { }
        if ($threads -gt 0 -and $mem -gt 0) { $live += $p } else { $zombie += $p }
    }

    if ($zombie.Count -gt 0) {
        Write-Host ("  残留进程条目 {0} 个（无线程/无内存，不影响文件）: {1}" -f
            $zombie.Count, (($zombie | ForEach-Object { $_.Id }) -join ', ')) -ForegroundColor DarkGray
    }

    if ($live.Count -gt 0) {
        $ids = ($live | ForEach-Object { $_.Id }) -join ', '
        if ($Kill) {
            Write-Step "结束活进程: $ids"
            foreach ($p in $live) {
                try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {
                    Write-Host ("  结束 PID {0} 失败: {1}" -f $p.Id, $_.Exception.Message) -ForegroundColor DarkYellow
                }
            }
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $remaining = @(Get-Process -Id ($live | ForEach-Object { $_.Id }) -ErrorAction SilentlyContinue)
                if ($remaining.Count -eq 0) { break }
                Start-Sleep -Milliseconds 300
            }
        }
        elseif ($Force) {
            Write-Host "  警告：游戏仍在运行（PID $ids），按 -Force 继续尝试覆盖" -ForegroundColor Yellow
        }
        else {
            throw "游戏仍在运行（PID $ids）。请先完全退出游戏，或加 -Kill 强制结束。"
        }
    }

    # ---------- 5. 备份 + 覆盖（带重试） ----------
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    if ($dllChanged -and (Test-Path -LiteralPath $targetDll)) {
        $backup = "$targetDll.bak_$stamp"
        Copy-Item -LiteralPath $targetDll -Destination $backup -Force
        Write-Host "  已备份旧 DLL: $(Split-Path -Leaf $backup)" -ForegroundColor DarkGray
    }
    if ($manifestChanged -and (Test-Path -LiteralPath $targetManifest)) {
        Copy-Item -LiteralPath $targetManifest -Destination "$targetManifest.bak_$stamp" -Force
        Write-Host "  已备份旧 JSON" -ForegroundColor DarkGray
    }

    function Copy-WithRetry([string]$Source, [string]$Destination, [string]$Label) {
        for ($i = 1; $i -le 10; $i++) {
            try {
                Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
                return
            }
            catch {
                if ($i -eq 10) {
                    throw "写入 $Label 失败（文件被占用？请确认游戏已完全退出）: $($_.Exception.Message)"
                }
                Write-Host ("  第 {0} 次写入 {1} 失败，等 1 秒重试..." -f $i, $Label) -ForegroundColor DarkYellow
                Start-Sleep -Seconds 1
            }
        }
    }

    if ($dllChanged) {
        Copy-WithRetry $builtDll $targetDll 'Sts2Companion.dll'
    }
    if ($manifestChanged) {
        Copy-WithRetry $sourceManifest $targetManifest 'Sts2Companion.json'
    }

    # ---------- 6. 校验 ----------
    if ($dllChanged) {
        $h1 = (Get-FileHash -LiteralPath $builtDll).Hash
        $h2 = (Get-FileHash -LiteralPath $targetDll).Hash
        if ($h1 -ne $h2) { throw "哈希不一致，部署可能不完整" }
    }

    $info = Get-Item -LiteralPath $targetDll
    Write-Host ("  部署成功: {0} 字节  {1}" -f $info.Length, $info.LastWriteTime) -ForegroundColor Green
    if (Test-Path -LiteralPath (Join-Path $ModsDir "Sts2Companion.dll.bak_$stamp")) {
        Write-Host "  备份: Sts2Companion.dll.bak_$stamp" -ForegroundColor DarkGray
    }
}

# ---------- 7. 可选：启动游戏 ----------
if ($Launch) {
    $existing = @(Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        Write-Host "  游戏已在运行，不重复启动。" -ForegroundColor Yellow
    }
    else {
        $gameRoot = Split-Path -Parent $GameDir
        $exe = Join-Path $gameRoot 'SlayTheSpire2.exe'
        if (Test-Path -LiteralPath $exe) {
            Start-Process -FilePath $exe
            Write-Host "  已启动游戏: $exe" -ForegroundColor Green
        }
        else {
            Write-Warning "找不到游戏可执行文件: $exe，请手动启动。"
        }
    }
}
else {
    Write-Host "  记得重启游戏才会加载新 DLL。" -ForegroundColor Yellow
}

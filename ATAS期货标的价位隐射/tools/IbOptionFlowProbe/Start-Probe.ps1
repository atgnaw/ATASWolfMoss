param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$probeExitCode = 0
function Read-Default([string]$Prompt, [string]$Default) {
    $answer = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer
}
try {
    $executable = Join-Path $PSScriptRoot 'IbOptionFlowProbe.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw '请从发布包运行此脚本；找不到 IbOptionFlowProbe.exe。' }
    if ($CheckOnly) {
        & $executable check-runtime
        if ($LASTEXITCODE -ne 0) { throw '独立程序运行环境检查失败。' }
    } else {
        Write-Host '只读市场数据验证；请先停止 ATAS 的 IB 数据列，按所选 ATM 范围预留行情线。'
        $symbols = (Read-Default '品种：QQQ,SPX 或单个品种' 'QQQ,SPX').ToUpperInvariant().Split(',')
        $spots = @{}
        foreach ($symbol in $symbols) {
            $ticker = $symbol.Trim()
            if ($ticker -notin @('QQQ', 'SPX') -or $spots.ContainsKey($ticker)) { throw '品种无效或重复。' }
            $spot = [decimal]::Parse((Read-Host "$ticker 当前参考现价（用于锁定 ATM，不自动换档）"), [cultureinfo]::InvariantCulture)
            if ($spot -le 0) { throw '参考现价必须大于零。' }
            $spots[$ticker] = $spot
        }
        $rangeText = Read-Default 'ATM 上下各多少档执行价（0–20；0 只采 ATM；所有品种使用相同档数）' '1'
        $eachSide = 0
        if (-not [int]::TryParse($rangeText, [ref]$eachSide) -or $eachSide -lt 0 -or $eachSide -gt 20) {
            throw 'ATM 上下各档数必须为 0–20 的整数。'
        }
        $config = @{
            strikes_each_side = $eachSide
            host = Read-Default 'Gateway 主机' '127.0.0.1'
            port = [int](Read-Default 'Gateway 端口' '4001')
            client_id = [int](Read-Default '独立 Client ID（不可使用 0 或 Master ID）' '2290')
            duration_minutes = [int](Read-Default '采集分钟数' '30')
            reference_spots = $spots
        }
        if ($config.port -lt 1 -or $config.port -gt 65535 -or $config.client_id -le 0 -or
            $config.duration_minutes -lt 1 -or $config.duration_minutes -gt 240) {
            throw '端口需为 1–65535，Client ID 必须大于 0，采集时长需为 1–240 分钟。'
        }
        Write-Host ($config | ConvertTo-Json -Depth 4)
        Write-Host "每品种 ATM + 上下各 $eachSide 档，共 $(2 * $eachSide + 1) 个执行价，均采 Call/Put；合计需要 $($spots.Count * (2 * $eachSide + 1) * 2) 条期权线。独立 ID 不会增加账户额度，请确认可用额度。"
        if ((Read-Host '输入 START 显式开始真实采集，其他输入退出') -ceq 'START') {
            $configRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WolfMoss/IbOptionFlowProbe/configs'
            [IO.Directory]::CreateDirectory($configRoot) | Out-Null
            $configPath = Join-Path $configRoot "$([guid]::NewGuid().ToString('N')).json"
            [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
            & $executable capture --live --config $configPath
            Write-Host "退出码：$LASTEXITCODE；配置留存在 $configPath"
            $probeExitCode = $LASTEXITCODE
            switch ($probeExitCode) {
                3 { Write-Host '采集成功，但报告生成失败；保留原始文件即可重新分析，无需重新采集。' -ForegroundColor Yellow }
                2 { Write-Host '采集未完整完成，请保留上方提示及输出文件。' -ForegroundColor Yellow }
                4 { Write-Host '采集未完整完成，且报告生成失败；请保留原始文件。' -ForegroundColor Yellow }
                0 { }
                default { Write-Host '程序执行失败，请查看上方具体提示并保留输出文件。' -ForegroundColor Yellow }
            }
        } else {
            Write-Host '已取消，未连接 Gateway。'
        }
    }
} catch {
    $probeExitCode = 1
    Write-Host ("启动失败：" + $_.Exception.Message) -ForegroundColor Red
    Write-Host '请截图保存上方提示；无需修改系统全局安全设置。' -ForegroundColor Yellow
} finally {
    if (-not $CheckOnly) { Read-Host '按回车关闭' | Out-Null }
}
exit $probeExitCode

<#
.SYNOPSIS
    TraceMemo Local HTTP API 一键探针 (M0③)。
.DESCRIPTION
    依次验证：健康检查 -> 联系人列表 -> 昵称解析 -> 拉取历史消息。
    用于判定冷链路（历史接入 -> 画像蒸馏）是否可行 (go/no-go)。
.PARAMETER Token
    TraceMemo「API Center」里复制的 Bearer Token。不传则交互式提示输入。
.PARAMETER BaseUrl
    Local HTTP API 基址，默认 http://127.0.0.1:6131/api/v1。
.PARAMETER Contact
    要解析/拉历史的联系人昵称或 wxid（中文可直接传，脚本会自动 URL 编码）。
.PARAMETER TimeRange
    历史时间范围，形如 2026-09-01~2026-09-28。不传则默认最近 30 天。
.EXAMPLE
    .\probe-tracememo.ps1
    .\probe-tracememo.ps1 -Token "abc123" -Contact "张三"
#>
param(
    [string]$Token,
    [string]$BaseUrl = "http://127.0.0.1:6131/api/v1",
    [string]$Contact,
    [string]$TimeRange
)

function Write-Section([string]$t) {
    Write-Host ""
    Write-Host "== $t ==" -ForegroundColor Cyan
}

function Invoke-Probe {
    param([string]$Name, [string]$Uri, [hashtable]$Headers)
    Write-Section $Name
    Write-Host "GET $Uri" -ForegroundColor DarkGray
    try {
        if ($Headers -and $Headers.Count -gt 0) {
            $r = Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec 10
        } else {
            $r = Invoke-RestMethod -Uri $Uri -TimeoutSec 10
        }
        $json = $r | ConvertTo-Json -Depth 6
        if ([string]::IsNullOrWhiteSpace($json)) { $json = "(空响应)" }
        Write-Host $json
        return $true
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $code = [int]$resp.StatusCode
            Write-Host "失败: HTTP $code" -ForegroundColor Red
            try {
                $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                Write-Host $sr.ReadToEnd() -ForegroundColor Yellow
            } catch { }
        } else {
            Write-Host ("失败: " + $_.Exception.Message) -ForegroundColor Red
        }
        return $false
    }
}

Write-Host "TraceMemo API 探针   base = $BaseUrl" -ForegroundColor Green

# 1) 健康检查（公开，无需 Token）
$healthOk = Invoke-Probe "1) 健康检查 /health (无需 Token)" "$BaseUrl/health" @{}
if (-not $healthOk) {
    Write-Host ""
    Write-Host "健康检查失败：TraceMemo 可能未运行，或未在 $BaseUrl 监听。" -ForegroundColor Yellow
    Write-Host "请确认：TraceMemo 已启动、已连接微信、且已开启 Local HTTP API。" -ForegroundColor Yellow
    return
}

# 获取 Token
if ([string]::IsNullOrWhiteSpace($Token)) {
    $sec = Read-Host "请粘贴 TraceMemo「API Center」里的 Token" -AsSecureString
    $Token = [System.Net.NetworkCredential]::new("", $sec).Password
}
$h = @{ Authorization = "Bearer $Token" }

# 2) 联系人列表
Invoke-Probe "2) 联系人列表 /contact?type=user" "$BaseUrl/contact?type=user" $h | Out-Null

# 3) 昵称 -> 会话解析
if (-not [string]::IsNullOrWhiteSpace($Contact)) {
    $q = [uri]::EscapeDataString($Contact)
    Invoke-Probe "3) 解析联系人 /resolve?q=$Contact" "$BaseUrl/resolve?q=$q" $h | Out-Null

    # 4) 拉取历史消息
    if ([string]::IsNullOrWhiteSpace($TimeRange)) {
        $end = Get-Date -Format "yyyy-MM-dd"
        $start = (Get-Date).AddDays(-30).ToString("yyyy-MM-dd")
        $TimeRange = "$start~$end"
    }
    $talker = [uri]::EscapeDataString($Contact)
    $time = [uri]::EscapeDataString($TimeRange)
    Invoke-Probe "4) 历史消息 /chatlog?talker=$Contact&time=$TimeRange" "$BaseUrl/chatlog?talker=$talker&time=$time" $h | Out-Null
} else {
    Write-Host ""
    Write-Host "提示：加 -Contact `"昵称`" 可继续验证 /resolve 与 /chatlog。" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "探针结束。把上面的输出（成功或报错原文）发我，用于汇总 M0 go/no-go。" -ForegroundColor Green


param(
    [string[]]$SourceRoots = @(
        "C:\Users\liavh\OneDrive - combtas\Desktop\Glassix_Backup_2024",
        "C:\Users\liavh\OneDrive - combtas\Desktop\Glassix_Backup_2025"
    ),
    [string]$OutDir = "C:\Users\liavh\OneDrive - combtas\Desktop\tripexinvoice\tools\finetune-data-prep\out",
    [int]$MaxHistoryTurns = 8,
    [int]$MinCompletionChars = 20,
    [int]$PilotSampleSize = 3000
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$fullJsonlPath   = Join-Path $OutDir "milo_finetune_full.jsonl"
$pilotJsonlPath  = Join-Path $OutDir "milo_finetune_pilot.jsonl"
$summaryPath     = Join-Path $OutDir "run_summary.txt"
$sampleTxtPath   = Join-Path $OutDir "sample_pairs_preview.txt"

# ── Milo's real persona/instruction header, reused verbatim (trimmed of runtime-only
#    sections like navigation/page-links and RAG context) from ChatService.BuildSystemPrompt
#    so training-time prompt shape matches production inference-time prompt shape. ──
$SystemHeader = @'
You are Milo - a friendly, professional customer-service assistant for TripEX (Travel & Expense Management). Your job is to HELP users understand and use the TripEX system: answer their questions, explain how features work, and help troubleshoot problems. Be warm, patient and clear.

CRITICAL OUTPUT RULE: Respond with ONLY a JSON object. No reasoning, no markdown, no text outside the JSON.
CRITICAL TEXT RULE: The "text" field must ALWAYS contain natural, human-readable text.
CRITICAL LANGUAGE RULE: Detect the language of the user's latest message and reply in that SAME language (Hebrew -> Hebrew, English -> English, etc.). Never switch languages on your own - mirror the user.

Output format (ONLY this JSON, nothing else): {"intent": "<help|escalate|general>", "text": "<your detailed, friendly answer>"}

Below is the conversation so far. Continue it as Milo, replying to the last Customer message.
'@

if (Test-Path $fullJsonlPath) { Remove-Item $fullJsonlPath -Force }
if (Test-Path $sampleTxtPath) { Remove-Item $sampleTxtPath -Force }

# ── counters ──
$stats = [ordered]@{
    TicketsScanned          = 0
    TicketsExcluded_Spam    = 0
    TicketsExcluded_NoAgentReply = 0
    TicketsExcluded_ParseError  = 0
    TicketsKept             = 0
    MessagesScanned         = 0
    MessagesKept_Message    = 0
    PairsEmitted            = 0
    PairsDropped_TooShort   = 0
    PairsDropped_Dedup      = 0
}

$seenCompletionHashes = New-Object 'System.Collections.Generic.HashSet[string]'
$sha256 = [System.Security.Cryptography.SHA256]::Create()

function Get-NormalizedHash([string]$text) {
    $norm = ($text.ToLowerInvariant() -replace '\s+', ' ').Trim()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($norm)
    return [Convert]::ToBase64String($sha256.ComputeHash($bytes))
}

# ── regexes for quote/signature stripping ──
$quoteHeaderPatterns = @(
    '(?m)^From:\s.*\r?\n^Sent:\s.*(\r?\n^To:\s.*)?(\r?\n^Cc:.*)?(\r?\n^Subject:\s.*)?',
    '(?m)^מאת:\s.*\r?\n^נשלח:\s.*',
    '(?m)^-{3,}\s*Original Message\s*-{3,}',
    '(?m)^-{3,}\s*הודעה מקורית\s*-{3,}',
    '(?m)^On .{3,80} wrote:\s*$',
    '(?m)^בתאריך .{3,80} כתב.{0,3}:\s*$',
    '(?m)^From:\s.*$'
)
$bannerPatterns = @(
    "You don't often get email from.*",
    "CAUTION: This message originated from outside.*",
    "זהירות: הודעה זו הגיעה ממקור חיצוני.*"
)

function Clean-MessageBody([string]$body) {
    if (-not $body) { return "" }
    $earliestCut = $body.Length
    foreach ($p in $quoteHeaderPatterns) {
        $m = [regex]::Match($body, $p)
        if ($m.Success -and $m.Index -lt $earliestCut) { $earliestCut = $m.Index }
    }
    $body = $body.Substring(0, $earliestCut)
    foreach ($p in $bannerPatterns) {
        $body = [regex]::Replace($body, $p, '', 'IgnoreCase')
    }
    return $body.Trim()
}

function Strip-CrossMessageSignatures([System.Collections.Generic.List[string]]$bodiesBySameSender) {
    # For a set of message bodies from the same sender in one ticket, find a trailing
    # line-run that recurs verbatim in >= 2 of them and strip it from all.
    if ($bodiesBySameSender.Count -lt 2) { return $bodiesBySameSender }
    $lineSets = $bodiesBySameSender | ForEach-Object { , ($_ -split "`n") }
    for ($n = 8; $n -ge 1; $n--) {
        $tails = @{}
        foreach ($lines in $lineSets) {
            if ($lines.Count -ge $n) {
                $tail = ($lines[($lines.Count - $n)..($lines.Count - 1)] -join "`n").Trim()
                if ($tail.Length -ge 10) {
                    if (-not $tails.ContainsKey($tail)) { $tails[$tail] = 0 }
                    $tails[$tail]++
                }
            }
        }
        $hit = $tails.GetEnumerator() | Where-Object { $_.Value -ge 2 } | Select-Object -First 1
        if ($hit) {
            $sig = $hit.Key
            for ($i = 0; $i -lt $bodiesBySameSender.Count; $i++) {
                if ($bodiesBySameSender[$i].TrimEnd().EndsWith($sig)) {
                    $idx = $bodiesBySameSender[$i].TrimEnd().LastIndexOf($sig)
                    $bodiesBySameSender[$i] = $bodiesBySameSender[$i].Substring(0, $idx).Trim()
                }
            }
            return $bodiesBySameSender
        }
    }
    return $bodiesBySameSender
}

function Scrub-Pii([string]$text, [string[]]$names) {
    if (-not $text) { return $text }
    $text = [regex]::Replace($text, '[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}', '[EMAIL]')
    $text = [regex]::Replace($text, '(\+972[\s\-]?\d{1,2}[\s\-]?\d{3}[\s\-]?\d{4})|(0\d{1,2}[\s\-]?\d{7})', '[PHONE]')
    foreach ($n in ($names | Sort-Object { $_.Length } -Descending)) {
        if ($n -and $n.Trim().Length -ge 3) {
            $text = $text.Replace($n, '[NAME]')
        }
    }
    return $text
}

# ── message header line: [datetime] name [type] (transactionType): ──
$msgHeaderRegex = '^\[(?<dt>[^\]]+)\]\s(?<name>.*?)\s\[(?<type>[^\]]+)\]\s\((?<ttype>[^)]+)\):\s*$'

$allFiles = foreach ($root in $SourceRoots) {
    if (Test-Path $root) {
        Get-ChildItem -Path $root -Recurse -Filter "*.txt" -File | Where-Object { $_.DirectoryName -like "*conversations*" }
    }
}
Write-Output "Found $($allFiles.Count) conversation files across $($SourceRoots.Count) source roots."

$writer = New-Object System.IO.StreamWriter($fullJsonlPath, $false, (New-Object System.Text.UTF8Encoding($false)))
$previewCount = 0

foreach ($file in $allFiles) {
    $stats.TicketsScanned++
    try {
        $raw = [System.IO.File]::ReadAllText($file.FullName)
    } catch {
        $stats.TicketsExcluded_ParseError++
        continue
    }

    $lines = $raw -split "`r?`n"
    $tags = ""
    $participantNames = New-Object System.Collections.Generic.List[string]
    $msgStartIdx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^Tags:\s(.+)$') { $tags = $Matches[1] }
        if ($lines[$i] -match '^\s*-\s(.+?)\s\[') { $participantNames.Add($Matches[1].Trim()) }
        if ($lines[$i] -eq "=== Messages ===") { $msgStartIdx = $i + 1; break }
    }
    if ($msgStartIdx -lt 0) { $stats.TicketsExcluded_ParseError++; continue }
    if ($tags -match '(?i)spam') { $stats.TicketsExcluded_Spam++; continue }

    # ── split into message blocks ──
    $blocks = New-Object System.Collections.Generic.List[object]
    $curHeader = $null
    $curBodyLines = New-Object System.Collections.Generic.List[string]
    for ($i = $msgStartIdx; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $msgHeaderRegex) {
            if ($curHeader) {
                $blocks.Add([PSCustomObject]@{ Header = $curHeader; Body = ($curBodyLines -join "`n") })
            }
            $curHeader = [PSCustomObject]@{ Name = $Matches['name'].Trim(); Type = $Matches['type']; TType = $Matches['ttype'] }
            $curBodyLines = New-Object System.Collections.Generic.List[string]
        } elseif ($curHeader) {
            if ($lines[$i] -notmatch '^\s*\[attachment:') { $curBodyLines.Add($lines[$i]) }
        }
    }
    if ($curHeader) { $blocks.Add([PSCustomObject]@{ Header = $curHeader; Body = ($curBodyLines -join "`n") }) }

    $stats.MessagesScanned += $blocks.Count
    $messages = $blocks | Where-Object { $_.Header.TType -eq "Message" }
    $stats.MessagesKept_Message += $messages.Count

    $hasAgentReply = ($messages | Where-Object { $_.Header.Type -eq "User" }).Count -gt 0
    $hasCustomerMsg = ($messages | Where-Object { $_.Header.Type -eq "Client" }).Count -gt 0
    if (-not $hasAgentReply -or -not $hasCustomerMsg) { $stats.TicketsExcluded_NoAgentReply++; continue }

    $stats.TicketsKept++

    # ── clean bodies, then strip cross-message signatures per sender ──
    $cleaned = foreach ($m in $messages) {
        [PSCustomObject]@{ Name = $m.Header.Name; Type = $m.Header.Type; Body = Clean-MessageBody $m.Body }
    }
    $bySender = $cleaned | Group-Object Name
    $signatureStripped = New-Object System.Collections.Generic.List[object]
    foreach ($grp in $bySender) {
        $bodyList = New-Object System.Collections.Generic.List[string]
        foreach ($m in $grp.Group) { $bodyList.Add($m.Body) }
        $strippedBodies = Strip-CrossMessageSignatures $bodyList
        for ($i = 0; $i -lt $grp.Group.Count; $i++) {
            $signatureStripped.Add([PSCustomObject]@{ Name = $grp.Group[$i].Name; Type = $grp.Group[$i].Type; Body = $strippedBodies[$i] })
        }
    }
    # restore original chronological order (signature stripping above was applied per-sender,
    # grouped out of order, so re-zip using a per-sender cursor rather than value-matching).
    $chrono = New-Object System.Collections.Generic.List[object]
    $perSenderCursor = @{}
    foreach ($m in $cleaned) {
        if (-not $perSenderCursor.ContainsKey($m.Name)) { $perSenderCursor[$m.Name] = 0 }
        $grpMatch = $signatureStripped | Where-Object { $_.Name -eq $m.Name }
        $idx = $perSenderCursor[$m.Name]
        $body = if ($idx -lt $grpMatch.Count) { $grpMatch[$idx].Body } else { $m.Body }
        $perSenderCursor[$m.Name] = $idx + 1
        $chrono.Add([PSCustomObject]@{ Name = $m.Name; Type = $m.Type; Body = (Scrub-Pii $body $participantNames.ToArray()) })
    }

    # ── slice into prompt/completion pairs ──
    $history = New-Object System.Collections.Generic.List[string]
    $sawCustomerTurn = $false
    foreach ($m in $chrono) {
        $roleLabel = if ($m.Type -eq "Client") { "Customer" } else { "Agent" }
        if ($roleLabel -eq "Customer") { $sawCustomerTurn = $true }

        if ($roleLabel -eq "Agent" -and $sawCustomerTurn -and $m.Body.Trim().Length -ge $MinCompletionChars) {
            $histSlice = $history | Select-Object -Last ($MaxHistoryTurns * 2)
            $promptText = $SystemHeader + "`n`n" + ($histSlice -join "`n") + "`nAgent:"
            $innerCompletion = [PSCustomObject]@{ intent = "help"; text = $m.Body.Trim() } | ConvertTo-Json -Compress
            $hash = Get-NormalizedHash $m.Body
            if ($seenCompletionHashes.Contains($hash)) {
                $stats.PairsDropped_Dedup++
            } else {
                [void]$seenCompletionHashes.Add($hash)
                $pairObj = [PSCustomObject]@{ prompt = $promptText; completion = $innerCompletion }
                $writer.WriteLine(($pairObj | ConvertTo-Json -Compress))
                $stats.PairsEmitted++
                if ($previewCount -lt 15) {
                    Add-Content -Path $sampleTxtPath -Value "----- PAIR $($previewCount+1) (ticket $($file.BaseName)) -----`nPROMPT:`n$promptText`n`nCOMPLETION:`n$innerCompletion`n" -Encoding utf8
                    $previewCount++
                }
            }
        } elseif ($roleLabel -eq "Agent") {
            $stats.PairsDropped_TooShort++
        }
        $history.Add("$roleLabel`: $($m.Body.Trim())")
    }

    if ($stats.TicketsScanned % 2000 -eq 0) {
        Write-Output "Progress: $($stats.TicketsScanned)/$($allFiles.Count) tickets scanned, $($stats.PairsEmitted) pairs so far"
    }
}
$writer.Close()

# ── write pilot sample ──
$allLines = Get-Content $fullJsonlPath
if ($allLines.Count -gt $PilotSampleSize) {
    $pilotLines = $allLines | Get-Random -Count $PilotSampleSize
} else {
    $pilotLines = $allLines
}
[System.IO.File]::WriteAllLines($pilotJsonlPath, $pilotLines, (New-Object System.Text.UTF8Encoding($false)))

# ── summary ──
$summary = @"
Glassix -> Milo fine-tune data prep — run summary
==================================================
Tickets scanned:              $($stats.TicketsScanned)
  excluded (SPAM tag):         $($stats.TicketsExcluded_Spam)
  excluded (no agent reply):   $($stats.TicketsExcluded_NoAgentReply)
  excluded (parse error):      $($stats.TicketsExcluded_ParseError)
  kept:                        $($stats.TicketsKept)
Messages scanned (all types): $($stats.MessagesScanned)
Messages kept (type=Message): $($stats.MessagesKept_Message)
Pairs emitted:                 $($stats.PairsEmitted)
  dropped (too short):         $($stats.PairsDropped_TooShort)
  dropped (dedup):             $($stats.PairsDropped_Dedup)

Full JSONL:  $fullJsonlPath  ($($allLines.Count) lines)
Pilot JSONL: $pilotJsonlPath ($($pilotLines.Count) lines)
Preview:     $sampleTxtPath
"@
$summary | Out-File -FilePath $summaryPath -Encoding utf8
Write-Output $summary

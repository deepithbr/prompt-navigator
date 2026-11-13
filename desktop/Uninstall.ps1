# Takes Prompt Advisor off this PC: stops the companion, removes its task and
# its three Claude Code hooks (with a backup of the settings file). Your task
# history in %LOCALAPPDATA%\PromptAdvisor stays unless you add -Purge. The
# browser extension is removed from the browser's own extensions page.
#
# -DryRun says what it would do and changes nothing.

param([switch]$DryRun, [switch]$Purge)

$desktop = Split-Path -Parent $MyInvocation.MyCommand.Path
$taskName = 'Prompt Advisor'
function Good($text) { Write-Host "  ok    $text" -ForegroundColor Green }
function Note($text) { Write-Host "  note  $text" -ForegroundColor Yellow }

Write-Host ''
Write-Host 'Removing Prompt Advisor'
if ($DryRun) { Write-Host '(dry run: nothing will be changed)' }

$running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'PromptAdvisor\.ps1' })
if ($DryRun) { Note "would stop $($running.Count) running copy(ies)" }
else { $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Good 'companion stopped' }

& schtasks.exe /Query /TN $taskName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Good 'no scheduled task to remove' }
elseif ($DryRun) { Note "would remove the task '$taskName'" }
else { & schtasks.exe /Delete /TN $taskName /F | Out-Null; Good "task '$taskName' removed" }

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not (Test-Path $settings)) { Good 'no Claude Code settings to change' }
elseif (-not $node) { Note "Node.js not found: remove the three hooks that post to 127.0.0.1:47823 from $settings by hand" }
elseif ($DryRun) { Note ("hooks are " + (& $node (Join-Path $desktop 'hooks.js') $settings check).Trim() + "; would remove them") }
else { $r = (& $node (Join-Path $desktop 'hooks.js') $settings remove).Trim(); Good "hooks $r in $settings" }

$data = Join-Path $env:LOCALAPPDATA 'PromptAdvisor'
if (-not $Purge) { Note "task history and pet settings kept in $data (run with -Purge to remove them)" }
elseif ($DryRun) { Note "would delete $data" }
else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Good "removed $data" }

Note 'remove the extension yourself: brave://extensions, Prompt Navigator for Claude, Remove'
Write-Host ''

# Sets up Prompt Advisor on this PC, or brings an existing setup up to date.
# Run Install.cmd in the repository folder. Safe to run again at any time.
#
#   1. Checks Node.js is there (the model hints need it; the tasks do not)
#   2. Checks the companion compiles on this PC
#   3. Registers the "Prompt Advisor" task: at log on, at every unlock, and
#      every 15 minutes, so it is always running, without a window
#   4. Adds the three Claude Code hooks to ~/.claude/settings.json, keeping
#      everything else in that file, with a backup if it changes
#   5. Starts the companion
#   6. Opens Brave's extensions page, with this folder's path copied, for
#      the one step Windows cannot do for you: Load unpacked
#
# -DryRun says what it would do and changes nothing.

param([switch]$DryRun)

# Each step checks its own result and carries on, so one problem does not
# stop the rest; native tools' messages are read, not thrown.
$ErrorActionPreference = 'Continue'
$desktop = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $desktop
$script = Join-Path $desktop 'PromptAdvisor.ps1'
$taskName = 'Prompt Advisor'
$problems = 0

function Say($text) { Write-Host $text }
function Good($text) { Write-Host "  ok    $text" -ForegroundColor Green }
function Note($text) { Write-Host "  note  $text" -ForegroundColor Yellow }
function Bad($text) { Write-Host "  fix   $text" -ForegroundColor Red; $script:problems++ }

Say ''
Say "Prompt Advisor setup, from $repo"
if ($DryRun) { Say '(dry run: nothing will be changed)' }
Say ''

# 1. Node.js
Say '1. Node.js'
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if ($node) { Good ("found " + (& $node --version) + " at $node") }
else { Note 'not found. The task board, the crane and the notifications work without it. The model hints in the desktop apps need it: install the LTS from https://nodejs.org, then run this again.' }

# 2. The companion compiles here
Say '2. The companion'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compile = @"
Add-Type -Path @('$desktop\PromptAdvisor.cs','$desktop\TaskBoard.cs','$desktop\Pet.cs') -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.Web.Extensions','$fw\WPF\UIAutomationClient.dll','$fw\WPF\UIAutomationTypes.dll','$fw\WPF\WindowsBase.dll')
"@
$out = & powershell -NoProfile -ExecutionPolicy Bypass -Command $compile 2>&1 | Out-String
if ($LASTEXITCODE -eq 0 -and $out.Trim() -eq '') { Good 'compiles on this PC' }
else { Bad ("does not compile here: " + ($out.Trim() -split "`n")[0]) }

# 3. Start by itself: the scheduled task
Say '3. Start with Windows'
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$conhost = Join-Path $env:WINDIR 'System32\conhost.exe'
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$esc = { param($s) [System.Security.SecurityElement]::Escape($s) }
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Prompt Advisor: model hints and the task board for Claude and ChatGPT. Starts at log on, at every unlock and every 15 minutes; a second copy exits at once.</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>$($me.User.Value)</UserId>
      <LogonType>InteractiveToken</LogonType>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>true</StartWhenAvailable>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
  </Settings>
  <Triggers>
    <LogonTrigger><UserId>$(& $esc $me.Name)</UserId></LogonTrigger>
    <SessionStateChangeTrigger><StateChange>SessionUnlock</StateChange><UserId>$(& $esc $me.Name)</UserId></SessionStateChangeTrigger>
    <TimeTrigger>
      <StartBoundary>$((Get-Date).Date.ToString('yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture))</StartBoundary>
      <Repetition><Interval>PT15M</Interval><StopAtDurationEnd>true</StopAtDurationEnd></Repetition>
    </TimeTrigger>
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>$(& $esc $conhost)</Command>
      <Arguments>$(& $esc "--headless $ps -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`"")</Arguments>
      <WorkingDirectory>$(& $esc $desktop)</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
if ($DryRun) { Note "would register the task '$taskName' to run $script" }
else {
  $tmp = Join-Path $env:TEMP 'prompt-advisor-task.xml'
  [IO.File]::WriteAllText($tmp, $xml, [Text.Encoding]::Unicode)
  $r = & cmd.exe /c "schtasks.exe /Create /TN `"$taskName`" /XML `"$tmp`" /F 2>&1" | Out-String
  Remove-Item $tmp -ErrorAction SilentlyContinue
  if ($LASTEXITCODE -eq 0) { Good "task '$taskName' registered: log on, unlock, every 15 minutes" }
  else { Bad ("could not register the task: " + $r.Trim()) }
}

# 4. Claude Code hooks
Say '4. Claude Code hooks'
$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
if (-not $node) { Note "skipped without Node.js. Add the three hooks shown in the README to $settings by hand, or install Node.js and run this again." }
else {
  $now = (& $node (Join-Path $desktop 'hooks.js') $settings check).Trim()
  if ($now -eq 'present') { Good "already in $settings" }
  elseif ($DryRun) { Note "would add three hooks to $settings (a backup is kept)" }
  else {
    New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
    $r = (& $node (Join-Path $desktop 'hooks.js') $settings add).Trim()
    if ($LASTEXITCODE -eq 0) { Good "hooks $r in $settings$(if ($r -eq 'added' -and (Test-Path "$settings.bak-prompt-advisor")) { '; the old file is settings.json.bak-prompt-advisor' })" }
    else { Bad "could not update $settings" }
  }
}

# 5. Start it, or restart it on the code in this folder
Say '5. Start the companion'
$running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'PromptAdvisor\.ps1' })
if ($DryRun) { Note $(if ($running.Count) { 'would restart the running companion' } else { 'would start the companion' }) }
else {
  $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  # The old copy lets go of its port within a few seconds; the new one waits for it.
  Start-Sleep -Seconds 2
  Start-Process -FilePath $conhost -ArgumentList @('--headless', $ps, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$script`"") -WindowStyle Hidden
  $up = $false
  for ($k = 0; $k -lt 30 -and -not $up; $k++) {
    Start-Sleep -Seconds 1
    try { $up = (Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:47823/ping' -Headers @{ 'X-Prompt-Advisor' = '1' } -TimeoutSec 2).StatusCode -eq 200 } catch { }
  }
  if ($up) { Good 'running: the crane appears on the taskbar when a task starts' }
  else { Bad "did not answer within 30 seconds; the reason is in $env:TEMP\PromptAdvisor.log" }
}

# 6. The browser extension
Say '6. The browser extension'
function BrowserPath {
  foreach ($key in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\brave.exe') {
    try { $p = (Get-ItemProperty $key -ErrorAction Stop).'(default)'; if ($p -and (Test-Path $p)) { return @($p, 'Brave', 'brave://extensions') } } catch { }
  }
  foreach ($p in "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe", "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe") { if (Test-Path $p) { return @($p, 'Brave', 'brave://extensions') } }
  foreach ($p in "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") { if (Test-Path $p) { return @($p, 'Chrome', 'chrome://extensions') } }
  return $null
}
# Already loaded from this folder? Brave and Chrome note an unpacked
# extension's folder in the profile's preferences; only that path is looked for.
$loaded = $false
foreach ($prefs in "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\User Data\Default\Secure Preferences", "$env:LOCALAPPDATA\Google\Chrome\User Data\Default\Secure Preferences") {
  if ((Test-Path $prefs) -and ([IO.File]::ReadAllText($prefs)).Contains(($repo -replace '\\', '\\'))) { $loaded = $true }
}
$browser = BrowserPath
if ($loaded) { Good 'already loaded from this folder. After pulling new code, press its reload arrow on the extensions page' }
elseif (-not $browser) { Note 'neither Brave nor Chrome found. Install one, then run this again for the extension' }
elseif ($DryRun) { Note "would open $($browser[1])'s extensions page with this folder's path copied" }
else {
  Set-Clipboard -Value $repo
  Start-Process -FilePath $browser[0] -ArgumentList $browser[2]
  Note "$($browser[1]) is open on its extensions page, and this folder's path is copied. Three clicks:"
  Say  '          a. turn on Developer mode, top right'
  Say  '          b. click Load unpacked'
  Say  '          c. paste the path into the folder box (Ctrl+V) and press Select Folder'
  Say  '        Then reload any open claude.ai or chatgpt.com tab, and sign in to claude.ai there'
}

Say ''
if ($problems) { Say "Done, with $problems thing(s) to fix above." } else { Say 'Done.' }
Say 'To take it all out again, run Uninstall.cmd.'

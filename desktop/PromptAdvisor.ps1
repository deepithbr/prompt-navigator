# Starts the Prompt Advisor desktop companion.
#
# Compiles PromptAdvisor.cs, TaskBoard.cs and Pet.cs in memory and runs it, because Smart App Control
# blocks unsigned exes built on this machine. Run it hidden with Start.cmd, or:
#   powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File PromptAdvisor.ps1
#
# It runs with no window, so each start and anything that stops it is written
# to %TEMP%\PromptAdvisor.log. A crash restarts it after ten seconds, up to
# five times in a row. Choosing Exit from the tray stops it for good.
#
# A crash restarts it as a fresh process, not by running it again in this one.
# The task board's listener and watcher threads would outlive a crash here and
# keep the port, so a second board in the same process could never listen.

# -Show, from Start.cmd: if a copy is already running, open its task list.
# The scheduled task's launches at logon and unlock leave that out.
param([int]$Attempt = 1, [switch]$Show)

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:TEMP 'PromptAdvisor.log'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

function Note($text) { "$(Get-Date -Format s)  $text" | Out-File -Append -Encoding UTF8 $log }

try {
  Add-Type -Path @((Join-Path $dir 'PromptAdvisor.cs'), (Join-Path $dir 'TaskBoard.cs'), (Join-Path $dir 'Pet.cs'))-ReferencedAssemblies @(
    'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions',
    (Join-Path $fw 'WPF\UIAutomationClient.dll'),
    (Join-Path $fw 'WPF\UIAutomationTypes.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll')
  ) -ErrorAction Stop
} catch {
  Note "could not compile: $($_.Exception.ToString())"
  exit 1
}

Note "started (attempt $Attempt)"
try {
  # Returns when you choose Exit, or at once if another copy is running.
  [AdvisorHost]::Run($dir, [bool]$Show)
  Note 'stopped'
} catch {
  Note "crashed: $($_.Exception.ToString())"
  if ($Attempt -lt 5) {
    Start-Sleep -Seconds 10
    $self = Join-Path $dir 'PromptAdvisor.ps1'
    Start-Process -FilePath (Join-Path $env:WINDIR 'System32\conhost.exe') -ArgumentList @(
      '--headless', 'powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
      '-File', "`"$self`"", '-Attempt', ($Attempt + 1))
  } else {
    Note 'crashed five times in a row; not restarting'
  }
  exit 1
}

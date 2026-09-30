# Starts the Prompt Advisor desktop companion.
#
# Compiles PromptAdvisor.cs in memory and runs it, because Smart App Control
# blocks unsigned exes built on this machine. Run it hidden with Start.cmd, or:
#   powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File PromptAdvisor.ps1
#
# It runs with no window, so each start and anything that stops it is written
# to %TEMP%\PromptAdvisor.log. A crash restarts it after ten seconds, up to
# five times. Choosing Exit from the tray stops it for good.

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:TEMP 'PromptAdvisor.log'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

function Note($text) { "$(Get-Date -Format s)  $text" | Out-File -Append -Encoding UTF8 $log }

try {
  Add-Type -TypeDefinition (Get-Content -Raw -Encoding UTF8 (Join-Path $dir 'PromptAdvisor.cs')) -ReferencedAssemblies @(
    'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions',
    (Join-Path $fw 'WPF\UIAutomationClient.dll'),
    (Join-Path $fw 'WPF\UIAutomationTypes.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll')
  ) -ErrorAction Stop
} catch {
  Note "could not compile: $($_.Exception.ToString())"
  exit 1
}

for ($attempt = 1; $attempt -le 5; $attempt++) {
  Note "started (attempt $attempt)"
  try {
    # Returns when you choose Exit, or at once if another copy is running.
    [AdvisorHost]::Run($dir)
    Note 'stopped'
    break
  } catch {
    Note "crashed: $($_.Exception.ToString())"
    Start-Sleep -Seconds 10
  }
}

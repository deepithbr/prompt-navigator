# Starts the Prompt Advisor desktop companion.
#
# Compiles PromptAdvisor.cs in memory and runs it, because Smart App Control
# blocks unsigned exes built on this machine. Run it hidden with Start.cmd, or:
#   powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File PromptAdvisor.ps1
#
# It runs with no window, so anything that stops it is written to
# %TEMP%\PromptAdvisor.log.

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $env:TEMP 'PromptAdvisor.log'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

try {
  Add-Type -TypeDefinition (Get-Content -Raw -Encoding UTF8 (Join-Path $dir 'PromptAdvisor.cs')) -ReferencedAssemblies @(
    'System.Windows.Forms', 'System.Drawing', 'System.Web.Extensions',
    (Join-Path $fw 'WPF\UIAutomationClient.dll'),
    (Join-Path $fw 'WPF\UIAutomationTypes.dll'),
    (Join-Path $fw 'WPF\WindowsBase.dll')
  ) -ErrorAction Stop
  [AdvisorHost]::Run($dir)
} catch {
  "$(Get-Date -Format s)  $($_.Exception.ToString())" | Out-File -Append -Encoding UTF8 $log
}

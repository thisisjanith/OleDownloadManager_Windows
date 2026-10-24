<#
.SYNOPSIS
    Minimal UI Automation driver used by the browser end-to-end test to confirm the Add Download dialog.
#>
param(
    [Parameter(Mandatory)] [int]$ProcessId,
    [Parameter(Mandatory)] [ValidateSet('set', 'click', 'wait')] [string]$Action,
    [Parameter(Mandatory)] [string]$AutomationId,
    [string]$Value,
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$root = $ae::RootElement
$byProcess = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $ProcessId)
$byId = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $AutomationId)

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$element = $null
while (-not $element -and (Get-Date) -lt $deadline) {
    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        if ($window.Current.AutomationId -eq $AutomationId) { $element = $window; break }
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byId)
        if ($element) { break }
    }
    if (-not $element) { Start-Sleep -Milliseconds 250 }
}
if (-not $element) { throw "UI element '$AutomationId' not found in process $ProcessId" }

switch ($Action) {
    'set'   { $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value) }
    'click' { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    'wait'  { }
}
Write-Output "ok"

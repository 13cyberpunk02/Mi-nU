# A video call with a real participant: video arrives and is drawn.
$prefix='vc'; . "$PSScriptRoot\common.ps1"

$a=Register $alice 'Alice Vc'; $b=Register $bob 'Bob Vc'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
$started=Get-Date
if ($env:VC_PATTERN -eq "1") { $env:MINU_TEST_PATTERN="1" }; $p=Start-Process -FilePath $exe -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,800)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Vc'; $null=W 'ChatInput'
  WClick $(if ($env:VC_BUTTON) { $env:VC_BUTTON } else { 'ChatCallVideo' })
  Start-Sleep 8
  Check 'app still running' (-not $proc.HasExited)
  Check 'call screen' ([bool](W 'CallHangUp' 3))
  if (W 'CallHangUp' 1) { WClick 'CallHangUp' }
} catch { $script:fail++; "ERROR: $_" } finally { Start-Sleep 2; if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$started} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in '.NET Runtime','Application Error' -and $_.Message -match 'MinU' } | Select-Object -First 2 | ForEach-Object { '--- ' + $_.ProviderName; ($_.Message -split "`n" | Select-Object -First 25) -join "`n" }

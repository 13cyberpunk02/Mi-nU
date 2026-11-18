# Windows notifications for messages while the app is in the background.
$prefix='ts'; . "$PSScriptRoot\common.ps1"
function ToastShows($text) { Until { foreach ($top in $WA::RootElement.FindAll($T::Children,$All)) { $owner=Get-Process -Id $top.Current.ProcessId -ErrorAction SilentlyContinue; if ($owner.ProcessName -notin 'ShellExperienceHost','ShellHost','explorer') { continue }; if ($top.FindFirst($T::Descendants,(New-Object $PC($WA::NameProperty,$text)))) { return $true }; foreach ($e in $top.FindAll($T::Descendants,(New-Object $PC($WA::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))) { if ($e.Current.Name -like "*$text*") { return $true } } }; $false } 15 }

# Windows itself may be showing no notifications (Do Not Disturb, or turned off): a toast of its own tells.
function SystemShowsToasts {
  [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
  [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
  $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
  $xml.LoadXml("<toast><visual><binding template=""ToastGeneric""><text>probe_$tag</text></binding></visual></toast>")
  [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($xml))
  Until { foreach ($top in $WA::RootElement.FindAll($T::Children, $All)) { if ($top.FindAll($T::Descendants, $All) | Where-Object { $_.Current.Name -like "*probe_$tag*" }) { return $true } }; $false } 10
}
if (-not (SystemShowsToasts)) { 'SKIP  Windows shows no notifications right now (Do Not Disturb?), so the app''s can''t be seen'; Summary; return }

$a=Register $alice 'Alice Ts'; $b=Register $bob 'Bob Ts'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized); Start-Sleep 2
  "  bob: $(Send "toast_net11_$tag")"
  Check 'toast shown while minimized' (ToastShows "toast_net11_$tag")
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

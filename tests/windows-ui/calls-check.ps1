# Calls: ringing, answering, declining, and the call records left in the chat.
$prefix='cr'; . "$PSScriptRoot\common.ps1"


$a=Register $alice 'Alice Cr'; $bobAuth=Register $bob 'Bob Cr'; $b=$bobAuth
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)

$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $proc) { "ERROR: the app didn't start"; return }
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $wp=$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,800)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit'; "  signed out the previous local account" }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Cr'
  Check 'chat opens' ([bool](W 'ChatInput'))

  "=== bob calls, nobody answers"
  $ring=Start-Process -FilePath $sender -ArgumentList $api,$bob,$pw,$chat.id,'call:1:4' -PassThru -WindowStyle Hidden
  Check 'incoming call screen' ([bool](W 'IncomingAccept' 10))
  $ring.WaitForExit(30000) | Out-Null
  Check 'incoming screen closes when he hangs up' (Until { -not (W 'IncomingAccept' 1) } 10)
  Check 'server: missed call record' (Until { (ChatMessages | Select-Object -Last 1).kind -eq 1 } 10)
  if (-not (W 'ChatInput' 3)) { WSelectItem 'Bob Cr' }
  Check 'chat: Пропущенный видеозвонок bubble' (WHas 'Пропущенный видеозвонок' 10)
  Check 'list: preview says missed' (Until { (ItemNames 'Bob Cr') -contains 'Пропущенный видеозвонок' } 10)

  "=== alice calls, bob answers for 3 s"
  $ans=Start-Process -FilePath $sender -ArgumentList $api,$bob,$pw,$chat.id,'answer:3' -PassThru -WindowStyle Hidden
  Start-Sleep 3
  WClick 'ChatCallAudio'
  Check 'call screen' ([bool](W 'CallHangUp' 10))
  $ans.WaitForExit(40000) | Out-Null
  Start-Sleep 1
  if (W 'CallHangUp' 2) { WClick 'CallHangUp' }
  Check 'server: answered record with a length' (Until { $l=ChatMessages | Select-Object -Last 1; $l.kind -eq 1 -and $l.call.durationSeconds -ge 2 } 15)
  if (-not (W 'ChatInput' 3)) { WSelectItem 'Bob Cr' }
  Check 'chat: Исходящий звонок bubble' (WHas 'Исходящий звонок' 10)
  $l=ChatMessages | Select-Object -Last 1; "  record: $($l.text), $($l.call.durationSeconds) s"
  Check 'list: preview with the length' (Until { (ItemNames 'Bob Cr') | Where-Object { $_ -like 'Исходящий звонок, 0:0*' } } 10)

  "=== a tap on the call calls back"
  $bubble=$null; $null=Until { $script:bubble=$win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'Исходящий звонок, *Нажмите*' } | Select-Object -First 1; [bool]$script:bubble } 5
  $item=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($bubble)
  while ($item -and $item.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { $item=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item) }
  if ($item) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
  Check 'call screen opens from the bubble' ([bool](W 'CallHangUp' 10))
  if (W 'CallHangUp' 2) { WClick 'CallHangUp' }
  Start-Sleep 2

  WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'
  Check 'signed out' ([bool](W 'LoginSubmit'))
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

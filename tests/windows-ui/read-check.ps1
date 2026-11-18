# Read receipts: ticks, unread counts, and marking read as messages are seen.
$prefix='rd'; . "$PSScriptRoot\common.ps1"

$a=Register $alice 'Alice Rd'; $b=Register $bob 'Bob Rd'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
1..3 | ForEach-Object { $null=Send "unread_$_`_$tag" }
Check 'server: three unread for alice' ((ChatAs $a).unreadCount -eq 3)

$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $proc) { "ERROR: the app didn't start"; return }
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  # Wide enough for the chat to open beside the list.
  $wp=$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,800)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit'; "  signed out the previous local account" }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  $ok=[bool](W 'MainNewChat'); if (-not $ok) { "  login error: $((W 'LoginError' 1).Current.Name); server field: $((W 'LoginServer' 1).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value); user: $((W 'LoginUserName' 1).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)" }
  Check 'signed in' $ok
  $null=Until { (ItemNames 'Bob Rd') -contains '3' } 15
  $names=ItemNames 'Bob Rd'; "  row: $($names -join ' | ')"
  Check 'list: badge shows 3' ($names -contains '3')

  WSelectItem 'Bob Rd'
  Check 'chat opens beside the list' ([bool](W 'ChatInput'))
  Check 'server: opening the chat reads it' (Until { (ChatAs $a).unreadCount -eq 0 } 15)
  $last=(Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/$($chat.id)/messages")[-1]
  Check 'server: bob sees alice read up to his last message' (Until { (ChatAs $b).readUpTo -eq $last.id } 10)
  Check 'list: badge gone' (Until { -not ((ItemNames 'Bob Rd') -contains '3') } 10)

  WSet 'ChatInput' "mine_$tag"; WClick 'ChatSend'
  Check 'own message: one tick (sent)' (WHas 'Отправлено' 10)
  $mine=(Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/$($chat.id)/messages")[-1]
  "  bob: $(Send "read:$($mine.id)")"
  Check 'own message: two ticks after bob reads' (WHas 'Прочитано' 10)

  "=== minimized: not read"
  $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized); Start-Sleep 2
  $null=Send "while_min_$tag"; Start-Sleep 4
  Check 'server: message stays unread while minimized' ((ChatAs $a).unreadCount -eq 1)
  $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal); [W.U]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null
  Check 'server: read once the window is back' (Until { (ChatAs $a).unreadCount -eq 0 } 15)

  "=== another chat open: counts up"
  Start-Sleep 65  # the server limits sign-ins per minute, and the sender signs in for every message
  $carol=Register "rd_carol_$tag" 'Carol Rd'
  $chat2=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($carol.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
  $null=& $sender $api "rd_carol_$tag" $pw $chat2.id "c1_$tag"; $null=& $sender $api "rd_carol_$tag" $pw $chat2.id "c2_$tag"
  Check 'list: other chat shows 2' (Until { (ItemNames 'Carol Rd') -contains '2' } 15)
  WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'
  Check 'signed out' ([bool](W 'LoginSubmit'))
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

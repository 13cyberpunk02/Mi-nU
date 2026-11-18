# Who is talking in a call, and the round audio tiles with the name under the avatar.
$prefix='sp'; . "$PSScriptRoot\common.ps1"

function Marks { @($win.FindAll($T::Descendants,$All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like '*, говорит' }) }
$a=Register $alice 'Alice Sp'; $b=Register $bob 'Bob Sp'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
$peer=Start-Process -FilePath $peerExe -ArgumentList $api,$bob,$pw,'40' -PassThru -WindowStyle Hidden -RedirectStandardOutput "$work\peer.log"
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,800)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Sp'; $null=W 'ChatInput'
  WClick 'ChatCallAudio'
  Check 'two tiles: Bob and me' (Until { (W 'CallTile0' 1).Current.Name -eq 'Bob Sp' -and (W 'CallTile1' 1).Current.Name -eq 'Вы' } 20)
  $n=(W 'CallTile0').Current.BoundingRectangle
  $av=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -eq 'Bob Sp' -and $_.Current.AutomationId -ne 'CallTile0' }) | ForEach-Object { $_.Current.BoundingRectangle } | Where-Object { $_.Width -ge 90 -and $_.Width -le 100 } | Select-Object -First 1
  "  avatar $([int]$av.X),$([int]$av.Y) $([int]$av.Width)x$([int]$av.Height); name $([int]$n.X),$([int]$n.Y) $([int]$n.Width)x$([int]$n.Height)"
  Check 'audio tile: round avatar 96, name under the avatar, centred' ($av -and [math]::Abs($av.Width - $av.Height) -lt 2 -and $n.Y -gt $av.Bottom -and [math]::Abs(($n.X + $n.Width/2) - ($av.X + $av.Width/2)) -le 3)
  Check 'connected' (Until { (W 'CallStatus' 1).Current.Name -match '^\d\d:\d\d$' } 20)
  Start-Sleep 3
  "  quiet: $((Marks) -join ', ')"
  Add-Type -AssemblyName System.Speech; $voice=New-Object System.Speech.Synthesis.SpeechSynthesizer; $voice.Volume=100; $null=$voice.SpeakAsync("Проверка подсветки говорящего. Раз, два, три. Сейчас идёт звонок, и меня должно быть хорошо слышно.")
  $seen=@(); $t0=Get-Date; while (((Get-Date)-$t0).TotalSeconds -lt 6) { $seen+=Marks; Start-Sleep -Milliseconds 300 }
  $seen=$seen | Sort-Object -Unique
  "  during the tone: $($seen -join ', ')"
  Check 'someone marked as talking during the tone' ($seen.Count -gt 0)
  Check 'marks go away once quiet' (Until { (Marks).Count -eq 0 } 10)
  if (W 'CallHangUp' 1) { WClick 'CallHangUp' }
} catch { $script:fail++; "ERROR: $_" } finally { Start-Sleep 2; if ($proc -and -not $proc.HasExited) { $proc.Kill() }; if (-not $peer.HasExited) { $peer.Kill() } }
Summary
'--- peer:'; Get-Content "$work\peer.log" -ErrorAction SilentlyContinue

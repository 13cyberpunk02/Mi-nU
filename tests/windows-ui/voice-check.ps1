# Voice messages: playing, pausing, recording, cancelling, sending, the waveform.
$prefix='vo'; . "$PSScriptRoot\common.ps1"


function ClickVoice { $v=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'Голосовое сообщение*' }); $r=$v[-1].Current.BoundingRectangle; [System.Windows.Forms.Cursor]::Position=New-Object System.Drawing.Point([int]($r.X+30),[int]($r.Y+$r.Height/2)); [M.C]::mouse_event(2,0,0,0,0); [M.C]::mouse_event(4,0,0,0,0) }
function VoiceTime { $e=W 'VoiceTime' 2; if ($e) { $e.Current.Name } }
$a=Register $alice 'Alice Vo'; $b=Register $bob 'Bob Vo'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
$outgoing="$appCache\outgoing"
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,850)
  [W.U]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Vo'; $null=W 'ChatInput'

  "=== mic or send"
  Check 'empty input shows the mic' ([bool](W 'ChatRecord' 3) -and -not (W 'ChatSend' 1))
  WSet 'ChatInput' 'hi'
  Check 'typing shows send' (Until { [bool](W 'ChatSend' 1) -and -not (W 'ChatRecord' 1) } 5)
  WSet 'ChatInput' ''

  "=== bob's voice message plays"
  "  bob: $(Send "voice:4:$assets\voice.m4a")"
  Check 'shown as a voice message of 0:04' (WHas 'Голосовое сообщение, 0:04' 15)
  Check 'server: length kept' ((ChatMessages | Select-Object -Last 1).attachments[0].durationSeconds -eq 4)
  Check 'chat list says so' (WHas 'Голосовое сообщение' 5)
  ClickVoice
  Check 'plays' (WHas 'Голосовое сообщение, 0:04, играет' 5)
  Start-Sleep 1; ClickVoice
  Check 'pauses' (WHas 'Голосовое сообщение, 0:04, на паузе' 5)
  Start-Sleep 2
  Check 'stays paused' (WHas 'Голосовое сообщение, 0:04, на паузе' 1)
  ClickVoice
  Check 'resumes' (WHas 'Голосовое сообщение, 0:04, играет' 5)
  Check 'plays to the end and resets' (WHas 'Голосовое сообщение, 0:04, нажмите' 10)
  $cached=Get-ChildItem "$appCache\files" -Filter *.m4a | Sort-Object LastWriteTime | Select-Object -Last 1
  Check 'kept in the cache as .m4a' ($cached -and $cached.Length -eq (Get-Item "$assets\voice.m4a").Length)
  "=== recording (kept on this machine, then thrown away)"
  $before=@(ChatMessages).Count
  WClick 'ChatRecord'
  Check 'recording bar shows' ([bool](W 'ChatRecordTime' 5))
  Start-Sleep 2.5
  $rt=(W 'ChatRecordTime').Current.Name; "  timer $rt"
  Check 'timer runs' ($rt -in '0:02','0:03')
  $file=Get-ChildItem $outgoing -Filter *.m4a -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  Check 'recording into a file' ([bool]$file)
  WClick 'ChatRecordCancel'
  Check 'cancel brings the input back' ([bool](W 'ChatRecord' 5))
  Check 'cancelled file deleted' (Until { -not (Test-Path $file.FullName) } 5)
  Start-Sleep 2
  Check 'nothing sent' (@(ChatMessages).Count -eq $before)

  "=== a real recording goes out"
  WClick 'ChatRecord'; $null=W 'ChatRecordTime' 5; Start-Sleep 3
  WClick 'ChatRecordSend'
  Check 'server: voice message arrives' (Until { $l=ChatMessages | Select-Object -Last 1; $l.sender.id -eq $a.user.id -and $l.attachments.Count -eq 1 } 20)
  $v=(ChatMessages | Select-Object -Last 1).attachments[0]; $own=(ChatMessages | Select-Object -Last 1).sender.id -eq $a.user.id; "  file: $($v.name) $($v.contentType) $($v.size) bytes, $($v.durationSeconds) s"
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$($v.id)/link"
  Invoke-WebRequest -UseBasicParsing $link.url -OutFile "$work\rec.m4a"
  $probe=& ffprobe -v error -show_entries stream=codec_name,channels,sample_rate:format=duration -of default=nw=1 "$work\rec.m4a" 2>&1; Remove-Item "$work\rec.m4a"
  "  probe: $($probe -join ' ')"
  Check 'AAC mono, length matches' ($own -and $v.durationSeconds -in 2,3,4 -and ($probe -join ' ') -match 'codec_name=aac' -and ($probe -join ' ') -match 'channels=1' -and [math]::Abs([double](($probe | Where-Object { $_ -like 'duration=*' }) -replace 'duration=','') - $v.durationSeconds) -lt 1)
  $wave=if ($v.waveform) { [Convert]::FromBase64String($v.waveform) } else { @() }
  "  waveform: $($wave.Length) bars, $($wave -join ' ')"
  # A quiet room gives an even line, so only that it was measured and within bounds is checked.
  Check 'waveform measured (20+ bars of 0..31)' ($wave.Length -ge 20 -and ($wave | Measure-Object -Maximum).Maximum -le 31)
  Check 'own recording shows as voice' (WHas "Голосовое сообщение, 0:0$($v.durationSeconds)" 5)
  Check 'sent recording removed from outgoing' (Until { @(Get-ChildItem $outgoing -Filter *.m4a -ErrorAction SilentlyContinue).Count -eq 0 } 5)
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

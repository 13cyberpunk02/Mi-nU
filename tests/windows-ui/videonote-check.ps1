# Round video messages: playing in the circle, recording, cancelling, sending.
$prefix='vn'; . "$PSScriptRoot\common.ps1"


function ClickNote { $v=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'Видеосообщение, *' }); $r=$v[-1].Current.BoundingRectangle; [System.Windows.Forms.Cursor]::Position=New-Object System.Drawing.Point([int]($r.X+$r.Width/2),[int]($r.Y+$r.Width/2)); [M.C]::mouse_event(2,0,0,0,0); [M.C]::mouse_event(4,0,0,0,0) }
$a=Register $alice 'Alice Vn'; $b=Register $bob 'Bob Vn'
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
  WSelectItem 'Bob Vn'; $null=W 'ChatInput'

  "=== the button"
  Check 'empty input shows the video message button' ([bool](W 'ChatVideoNote' 3))
  WSet 'ChatInput' 'hi'
  Check 'typing hides it' (Until { -not (W 'ChatVideoNote' 1) } 5)
  WSet 'ChatInput' ''

  "=== bob's video message plays in its circle"
  "  bob: $(Send "videonote:4:$assets\note.mp4")"
  Check 'shown as a video message of 0:04' (WHas 'Видеосообщение, 0:04, нажмите' 15)
  Check 'chat list says so' (WHas 'Видеосообщение' 5)
  $cache="$appCache\files"
  Check 'fetched by itself for the first frame' (Until { [bool](Get-ChildItem $cache -Filter *.mp4 -ErrorAction SilentlyContinue | Where-Object { $_.Length -eq (Get-Item "$assets\note.mp4").Length }) } 10)
  $circle=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'Видеосообщение, *' })[-1].Current.BoundingRectangle
  "  note $([int]$circle.X),$([int]$circle.Y) $([int]$circle.Width)x$([int]$circle.Height)"
  Check 'on the left, 230 wide' ($circle.X -lt 700 -and [math]::Abs($circle.Width - 230) -le 4)
  ClickNote
  Check 'plays' (WHas 'Видеосообщение, 0:04, играет' 5)
  Check 'plays to the end and resets' (WHas 'Видеосообщение, 0:04, нажмите' 10)
  ClickNote; $null=WHas 'Видеосообщение, 0:04, играет' 5; Start-Sleep 1; ClickNote
  Check 'a second tap stops it' (WHas 'Видеосообщение, 0:04, нажмите' 3)

  "=== recording, cancelled (stays on this machine, thrown away)"
  $before=@(ChatMessages).Count
  WClick 'ChatVideoNote'
  Check 'recording screen shows' ([bool](W 'VideoNoteRecordTime' 8))
  $err=W 'ChatError' 1; if ($err) { "  error: $($err.Current.Name)" }
  Start-Sleep 2.5
  "  timer $((W 'VideoNoteRecordTime').Current.Name)"
  Check 'timer runs' ((W 'VideoNoteRecordTime').Current.Name -in '0:02','0:03')
  Check 'recording into a file' ([bool](Get-ChildItem $outgoing -Filter *.mp4 -ErrorAction SilentlyContinue))
  WClick 'VideoNoteCancel'
  Check 'back to the chat' (Until { -not (W 'VideoNoteRecordTime' 1) } 5)
  Check 'cancelled file deleted' (Until { @(Get-ChildItem $outgoing -Filter *.mp4 -ErrorAction SilentlyContinue).Count -eq 0 } 5)
  Start-Sleep 1
  Check 'nothing sent' (@(ChatMessages).Count -eq $before)

  "=== a real one goes out (only its metadata is looked at)"
  WClick 'ChatVideoNote'; $null=W 'VideoNoteRecordTime' 8; Start-Sleep 3
  WClick 'VideoNoteSend'
  Check 'server: video message arrives' (Until { $l=ChatMessages | Select-Object -Last 1; $l.sender.id -eq $a.user.id -and $l.attachments.Count -eq 1 } 20)
  $v=(ChatMessages | Select-Object -Last 1).attachments[0]; "  file: $($v.name) $($v.contentType) $($v.size) bytes, $($v.durationSeconds) s"
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$($v.id)/link"
  Invoke-WebRequest -UseBasicParsing $link.url -OutFile "$work\rec.mp4"
  $probe=(& ffprobe -v error -show_entries stream=codec_type,codec_name:format=duration -of default=nw=1 "$work\rec.mp4" 2>&1) -join ' '; Remove-Item "$work\rec.mp4"
  "  probe: $probe"
  $len=[double](([regex]::Match($probe,'duration=([\d.]+)')).Groups[1].Value)
  Check 'H.264 with AAC sound, length matches the file' ($probe -match 'codec_name=h264' -and $probe -match 'codec_name=aac' -and [math]::Abs($len - $v.durationSeconds) -lt 1 -and $v.contentType -eq 'video/mp4')
  Check 'about a megabit a second or less' ($v.size * 8 / $len -lt 1400000)
  Check 'own video message in the chat' (WHas "Видеосообщение, 0:0$($v.durationSeconds)" 5)
  Check 'sent file removed from outgoing' (Until { @(Get-ChildItem $outgoing -Filter *.mp4 -ErrorAction SilentlyContinue).Count -eq 0 } 5)
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

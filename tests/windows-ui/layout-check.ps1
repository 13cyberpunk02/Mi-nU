# Layout: days in the middle, photos filling their bubble with the caption under them.
$prefix='ly'; . "$PSScriptRoot\common.ps1"


function Rect($e) { $e.Current.BoundingRectangle }
$a=Register $alice 'Alice Ly'; $b=Register $bob 'Bob Ly'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
# Days back: one message on the 10th of November, one yesterday, the rest today.
$null=Send "old_$tag"; $null=Send "yest_$tag"
"  psql: " + ((Psql "UPDATE ""Messages"" SET ""SentAt"" = now() - interval '7 days' WHERE ""Text"" = 'old_$tag'; UPDATE ""Messages"" SET ""SentAt"" = now() - interval '1 day' WHERE ""Text"" = 'yest_$tag';") -join ' ')
$null=Send "today_$tag"
$null=Send "photo:$assets\bob.png"
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,850)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Ly'; $null=W 'ChatInput'

  "=== alice sends a photo with a long caption"
  WSet 'ChatInput' "This caption is long enough that it has to wrap to the width of the photo above it $tag"
  WClick 'ChatAttach'; Pick 'Фото'
  PickFile "$assets\big.jpg"
  $null=Until { (ChatMessages | Select-Object -Last 1).attachments.Count -eq 1 } 30
  Start-Sleep 3

  "=== days in the middle"
  $sc=(W 'ChatMessages').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); for ($i=0;$i -lt 10;$i++) { try { $sc.Scroll('NoAmount','LargeDecrement') } catch {} }; Start-Sleep 2
  $list=Rect (W 'ChatMessages'); $mid=$list.X + $list.Width/2
  $week=(Get-Date).AddDays(-7).ToString('d MMMM', [Globalization.CultureInfo]'ru-RU')
  foreach ($day in 'Вчера','Сегодня',$week) {
    $e=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -eq $day -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text }) | Select-Object -First 1
    if (-not $e) { Check "day '$day' shown" $false; continue }
    $r=Rect $e; $off=[math]::Round(($r.X + $r.Width/2) - $mid,1)
    "  $day centre is $off px from the middle"
    Check "day '$day' centred" ([math]::Abs($off) -le 2)
  }

  "=== photo fills its bubble"
  for ($i=0;$i -lt 10;$i++) { try { $sc.Scroll('NoAmount','LargeIncrement') } catch {} }; Start-Sleep 2
  $photos=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -eq 'Фото, нажмите, чтобы открыть' })
  $mine=Rect $photos[-1]
  $cap=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'This caption*' }) | Select-Object -First 1
  $c=Rect $cap
  "  photo $([int]$mine.X),$([int]$mine.Y) $([int]$mine.Width)x$([int]$mine.Height); caption $([int]$c.X),$([int]$c.Y) $([int]$c.Width)x$([int]$c.Height)"
  Check 'photo as wide as the bubble (260)' ([math]::Abs($mine.Width - 260) -le 2)
  Check 'caption below the photo' ($c.Y -ge $mine.Bottom)
  Check 'caption wraps within the photo, inset 12' ($c.X -ge $mine.X + 10 -and $c.Right -le $mine.Right - 10 -and $c.Height -gt 25)
  Check 'own photo on the right side' ($mine.Right -gt $mid)
  $theirs=($photos | ForEach-Object { Rect $_ } | Sort-Object X | Select-Object -First 1)
  "  bob's photo $([int]$theirs.X) $([int]$theirs.Width)x$([int]$theirs.Height)"
  Check 'bob''s photo at least 200 wide, on the left' ($theirs.Width -ge 198 -and $theirs.X -lt $mid)
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

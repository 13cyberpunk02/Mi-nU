# Photos and files: picking, sending, the list preview, opening.
$prefix='ph'; . "$PSScriptRoot\common.ps1"


$a=Register $alice 'Alice Ph'; $b=Register $bob 'Bob Ph'
$chat=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/direct" -ContentType 'application/json' -Body (@{userId=$a.user.id}|ConvertTo-Json)
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,850)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem 'Bob Ph'; $null=W 'ChatInput'

  "=== alice sends a big photo with a caption"
  WSet 'ChatInput' "caption_$tag"
  WClick 'ChatAttach'; Pick 'Фото'
  PickFile "$assets\big.jpg"
  Check 'server: message with the photo and caption' (Until { $l=ChatMessages | Select-Object -Last 1; $l.attachments.Count -eq 1 -and $l.text -eq "caption_$tag" } 30)
  $l=ChatMessages | Select-Object -Last 1; $f=$l.attachments[0]
  "  file: $($f.name) $($f.contentType) $($f.size) bytes, $($f.width)x$($f.height)"
  Check 'made smaller on the way (at most 2048 wide)' ($f.width -le 2048 -and $f.width -gt 0)
  Check 'proportions kept (3:2)' ([math]::Abs($f.width/$f.height - 1.5) -lt 0.01)
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$($f.id)/link"
  $bytes=(Invoke-WebRequest -UseBasicParsing $link.url).Content
  "  downloaded: $($bytes.Length) bytes, jpeg $(JpegSize $bytes)"
  Check 'stored size matches the header' ((JpegSize $bytes) -eq "$($f.width)x$($f.height)")
  Check 'EXIF stripped (no SECRET-PLACE)' (-not [Text.Encoding]::ASCII.GetString($bytes).Contains('SECRET-PLACE'))
  Check 'chat shows the photo' (WHas 'Фото, нажмите, чтобы открыть' 10)

  "=== bob sends a photo"
  "  bob: $(Send "photo:$assets\bob.png")"
  $bobFile=(ChatMessages | Select-Object -Last 1).attachments[0]
  Check 'list preview says Фото' (Until { (ItemNames 'Bob Ph') -contains 'Фото' } 10)
  $cached=$null; $null=Until { $script:cached=Get-ChildItem "$env:LOCALAPPDATA" -Recurse -Filter "$($bobFile.id -replace '-','').png" -ErrorAction SilentlyContinue | Select-Object -First 1; [bool]$script:cached } 15
  Check 'downloaded into the cache by id' ([bool]$cached)
  if ($cached) { "  cache: $($cached.FullName) $($cached.Length) bytes" }

  "=== alice sends a file"
  WClick 'ChatAttach'; Pick 'Файл'
  PickFile "$assets\отчёт.txt"
  Check 'server: file message' (Until { $l=ChatMessages | Select-Object -Last 1; $l.attachments.Count -eq 1 -and $l.attachments[0].name -eq 'отчёт.txt' } 30)
  $f=(ChatMessages | Select-Object -Last 1).attachments[0]
  "  file: $($f.name) $($f.contentType) $($f.size) bytes"
  Check 'stored as a download' ($f.contentType -eq 'application/octet-stream')
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$($f.id)/link"
  $resp=Invoke-WebRequest -UseBasicParsing $link.url
  Check 'downloads with its name and content' ($resp.Headers['Content-Disposition'] -match 'attachment' -and [Text.Encoding]::UTF8.GetString($resp.Content).StartsWith('Отчёт для проверки'))
  Check 'chat shows the file line' (WHas 'Файл отчёт.txt' 10)
  Check 'list preview names the file' (Until { (ItemNames 'Bob Ph') | Where-Object { $_ -like 'Файл · отчёт.txt*' } } 10)

  "=== bob sends a file, alice opens it"
  "  bob: $(Send "file:$assets\bob.minutest")"
  Check 'bob''s file shows up' (WHas 'Файл bob.minutest' 10)
  Get-Process OpenWith -ErrorAction SilentlyContinue | Stop-Process -Force
  $el=@($win.FindAll($T::Descendants,$All) | Where-Object { $_.Current.Name -like 'Файл bob.minutest*' })[-1]
  $r=$el.Current.BoundingRectangle
  Add-Type -Namespace M -Name C -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(int f,int x,int y,int d,int e);'
  [System.Windows.Forms.Cursor]::Position=New-Object System.Drawing.Point([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2)); [M.C]::mouse_event(2,0,0,0,0); [M.C]::mouse_event(4,0,0,0,0)
  Check 'handed to Windows to open (Open with appears)' (Until { [bool](Get-Process OpenWith -ErrorAction SilentlyContinue) } 15)
  $cachedFile=Get-ChildItem "$appCache\files" -Filter '*.minutest' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  Check 'downloaded into the cache first' ([bool]$cachedFile -and (Get-Content $cachedFile.FullName -Raw) -eq 'from bob')
  Get-Process OpenWith -ErrorAction SilentlyContinue | Stop-Process -Force
  WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

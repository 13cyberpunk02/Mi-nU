# Group pictures: set, replaced by another member, removed.
$prefix='ga'; . "$PSScriptRoot\common.ps1"



function GroupOf($auth) { @(Invoke-RestMethod -Headers @{Authorization="Bearer $($auth.token)"} "$api/api/chats" | ForEach-Object { $_ }) | Where-Object { $_.id -eq $group.id } }
function Lines { @(Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/chats/$($group.id)/messages" | ForEach-Object { $_ }) | ForEach-Object { $_.text } }
$a=Register $alice 'Alice Ga'; $b=Register $bob 'Bob Ga'
$group=Invoke-RestMethod -Method Post -Headers @{Authorization="Bearer $($a.token)"} "$api/api/chats/group" -ContentType 'application/json' -Body (@{title="Team $tag"; memberIds=@($b.user.id)}|ConvertTo-Json)
$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
$cache="$appCache\files"
try {
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,850)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem "Team $tag"; $null=W 'ChatInput'

  "=== alice sets the group photo"
  WClick 'ChatGroupInfo'
  Check 'group page has a photo button' ([bool](W 'GroupPickPhoto' 8))
  Check 'no remove button yet' (-not (W 'GroupRemovePhoto' 1))
  WClick 'GroupPickPhoto'
  PickFile "$assets\big.jpg"
  $ok=Until { [bool](GroupOf $b).avatarId } 30; if (-not $ok) { $e=W 'GroupError' 1; "  page error: $(if ($e) { $e.Current.Name })"; "  dialogs: $((Get-Process PickerHost -ErrorAction SilentlyContinue).Count)" }
  Check 'server: bob sees the group photo' $ok
  $id=(GroupOf $b).avatarId
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$id/link"
  $bytes=(Invoke-WebRequest -UseBasicParsing $link.url).Content; "  photo: $($bytes.Length) bytes, jpeg $(JpegSize $bytes)"
  Check 'made small, no EXIF' ([int]((JpegSize $bytes) -split 'x')[0] -le 512 -and -not [Text.Encoding]::ASCII.GetString($bytes).Contains('SECRET-PLACE'))
  Check 'line in the chat' ((Lines) -contains 'Alice Ga изменил(а) фото группы')
  Check 'remove button appears' ([bool](W 'GroupRemovePhoto' 5))

  "=== bob changes it, alice's app follows"
  GoBack; $null=W 'ChatInput' 5
  $out=& curl.exe -s -o NUL -w '%{http_code}' -X PUT -H "Authorization: Bearer $($b.token)" -F "file=@$assets\bob.png;type=image/png" "$api/api/chats/$($group.id)/avatar"
  "  bob's upload: $out"
  $id2=(GroupOf $a).avatarId
  Check 'new photo id' ($id2 -and $id2 -ne $id)
  Check 'alice''s app downloads it by itself' (Until { Test-Path "$cache\$($id2 -replace '-','')*" } 15)
  Check 'old one is gone' ((& curl.exe -s -o NUL -w '%{http_code}' -H "Authorization: Bearer $($a.token)" "$api/api/files/$id/link") -eq '404')
  Check 'alice sees bob''s line' (WHas 'Bob Ga изменил(а) фото группы' 10)

  "=== alice removes it"
  WClick 'ChatGroupInfo'; $null=W 'GroupRemovePhoto' 8
  WClick 'GroupRemovePhoto'; WClickName 'Удалить'
  Check 'server: photo removed' (Until { -not (GroupOf $b).avatarId } 10)
  Check 'remove button hides' (Until { -not (W 'GroupRemovePhoto' 1) } 5)
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

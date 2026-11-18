# Profile: picture, name, removing the picture, signing out.
$prefix='av'; . "$PSScriptRoot\common.ps1"


function ChatOf($auth) { @(Invoke-RestMethod -Headers @{Authorization="Bearer $($auth.token)"} "$api/api/chats" | ForEach-Object { $_ }) | Where-Object { $_.id -eq $chat.id } }
$a=Register $alice 'Alice Av'; $b=Register $bob 'Bob Av'
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

  "=== profile: picture"
  WClick 'MainProfile'
  Check 'profile opens with the user name' (WHas "@$alice" 8)
  WClick 'ProfilePickPhoto'
  PickFile "$assets\big.jpg"
  Check 'server: bob sees alice''s picture' (Until { [bool]((ChatOf $b).members | Where-Object { $_.id -eq $a.user.id }).avatarId } 30)
  $avatarId=((ChatOf $b).members | Where-Object { $_.id -eq $a.user.id }).avatarId
  $link=Invoke-RestMethod -Headers @{Authorization="Bearer $($b.token)"} "$api/api/files/$avatarId/link"
  $bytes=(Invoke-WebRequest -UseBasicParsing $link.url).Content
  "  picture: $($bytes.Length) bytes, jpeg $(JpegSize $bytes)"
  Check 'made small (at most 512 wide)' ([int]((JpegSize $bytes) -split 'x')[0] -le 512)
  Check 'no EXIF in the picture' (-not [Text.Encoding]::ASCII.GetString($bytes).Contains('SECRET-PLACE'))
  Check 'remove button appears' ([bool](W 'ProfileRemovePhoto' 5))

  "=== profile: name"
  WSet 'ProfileName' "Alice Renamed $tag"; WClick 'ProfileSave'
  Check 'server: bob''s chat is titled with the new name' (Until { (ChatOf $b).title -eq "Alice Renamed $tag" } 10)

  "=== bob's picture shows in alice's list"
  GoBack
  Check 'back on the list' ([bool](W 'MainNewChat' 5))
  "  bob: $(Send "avatar:$assets\bob.png")"
  $bobAvatar=((ChatOf $a).members | Where-Object { $_.id -eq $b.user.id }).avatarId
  $cache="$appCache\files"
  Check 'alice''s app downloads bob''s new picture on its own' (Until { Test-Path "$cache\$($bobAvatar -replace '-','')" } 15)

  "=== remove own picture"
  WClick 'MainProfile'; $null=W 'ProfileRemovePhoto' 5
  WClick 'ProfileRemovePhoto'; WClickName 'Удалить'
  Check 'server: picture removed' (Until { -not ((ChatOf $b).members | Where-Object { $_.id -eq $a.user.id }).avatarId } 10)
  Check 'remove button hides' (Until { -not (W 'ProfileRemovePhoto' 1) } 5)

  "=== sign out from the profile"
  WClickName 'Выйти'
  Check 'back to sign-in' ([bool](W 'LoginSubmit' 10))
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

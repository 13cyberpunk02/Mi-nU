# Online status: "в сети" and "был(а)" in the chat header and the list.
$prefix='pr'; . "$PSScriptRoot\common.ps1"

function Presence($auth,$userId) { @(Invoke-RestMethod -Headers @{Authorization="Bearer $($auth.token)"} "$api/api/presence" | ForEach-Object { $_ }) | Where-Object { $_.userId -eq $userId } }
function Header { $e=W 'ChatTitle' 2; if (-not $e) { return @() }; @($win.FindAll($T::Descendants,$All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'был(а)*' -or $_ -eq 'в сети' }) }

$a=Register $alice 'Alice Pr'; $b=Register $bob 'Bob Pr'
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
  WSelectItem 'Bob Pr'
  Check 'chat opens' ([bool](W 'ChatInput'))
  Check 'never seen: no status under the name' ((Header).Count -eq 0)
  Check 'server: bob sees alice online' (Until { (Presence $b $a.user.id).online } 10)

  "=== bob comes online"
  $job=Start-Process -FilePath $sender -ArgumentList $api,$bob,$pw,$chat.id,'online:12' -PassThru -WindowStyle Hidden
  Check 'header: в сети' (Until { (Header) -contains 'в сети' } 10)
  Check 'list: online dot' (Until { (ItemNames 'Bob Pr') -contains 'В сети' } 5)
  $job.WaitForExit(30000) | Out-Null
  Check 'header: был(а) только что after he leaves' (Until { (Header) -contains 'был(а) только что' } 15)
  Check 'list: dot gone' (Until { -not ((ItemNames 'Bob Pr') -contains 'В сети') } 5)
  "  header: $((Header) -join ', ')"

  "=== alice minimizes"
  $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
  Check 'server: alice offline while minimized' (Until { -not (Presence $b $a.user.id).online } 10)
  Check 'server: with a last-seen time' ([bool](Presence $b $a.user.id).lastSeenAt)
  $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal); [W.U]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null
  Check 'server: alice online again' (Until { (Presence $b $a.user.id).online } 10)

  WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'
  Check 'signed out' ([bool](W 'LoginSubmit'))
  Check 'server: alice offline after signing out' (Until { -not (Presence $b $a.user.id).online } 10)
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

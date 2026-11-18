# Groups: creating, renaming, adding and removing members, leaving.
$prefix='gr'; . "$PSScriptRoot\common.ps1"

function RemoveButtonOf($text) { $i=Item $text; if ($i) { $i.FindFirst($T::Descendants,(New-Object $PC($WA::AutomationIdProperty,'GroupRemove'))) } }

$a=Register $alice 'Alice Gr'; $b=Register $bob 'Bob Gr'; $c=Register "gr_carol_$tag" 'Carol Gr'
$group=Api $a Post '/api/chats/group' @{title="Team $tag"; memberIds=@($b.user.id)}

$p=Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" -PassThru; Start-Sleep 12
$proc=Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $proc) { "ERROR: the app didn't start"; return }
$hwnd=$proc.MainWindowHandle
$win=$WA::RootElement.FindFirst($T::Children,(New-Object $PC($WA::ProcessIdProperty,$proc.Id)))
try {
  $wp=$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
  $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200,850)
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null=W 'LoginSubmit'; "  signed out the previous local account" }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $alice; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
  WSelectItem "Team $tag"
  Check 'group chat opens' ([bool](W 'ChatInput'))
  WClick 'ChatGroupInfo'
  Check 'group page opens' (WHas 'владелец' 10)
  Check 'group page: alice is owner' (Until { (ItemNames 'Alice Gr') -contains 'вы · владелец' } 5)

  "=== rename"
  WSet 'GroupTitle' "Crew $tag"; WClick 'GroupRename'
  Check 'page shows the new name' (Until { (W 'GroupName' 1).Current.Name -eq "Crew $tag" } 10)
  Check 'server: renamed, bob sees the line' (Until { $ch=ChatsOf $b | Where-Object { $_.id -eq $group.id }; $ch.title -eq "Crew $tag" -and $ch.lastMessage.text -eq "Alice Gr переименовал(а) группу в «Crew $tag»" } 10)

  "=== add carol"
  WClick 'GroupAdd'
  Check 'picker without the title field' ((W 'NewGroupSearch' 10) -and -not (W 'NewGroupTitle' 1))
  WSet 'NewGroupSearch' "gr_carol_$tag"; WSelectItem 'Carol Gr'; WClick 'NewGroupCreate'
  Check 'back on the group page with carol' (Until { [bool](Item 'Carol Gr') -and [bool](W 'GroupLeave' 1) } 15)
  Check 'server: carol is in' (Until { (ChatsOf $c | Where-Object { $_.id -eq $group.id }) } 10)

  "=== remove carol"
  $rb=RemoveButtonOf 'Carol Gr'; if (-not $rb) { throw 'no remove button for carol' }
  $rb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  WClickName 'Удалить'
  Check 'carol gone from the page' (Until { -not (Item 'Carol Gr') } 10)
  Check 'server: carol is out' (Until { -not (ChatsOf $c | Where-Object { $_.id -eq $group.id }) } 10)

  "=== bob renames: the page follows"
  $null=Api $b Put "/api/chats/$($group.id)/title" @{title="Squad $tag"}
  Check 'page follows a rename by someone else' (Until { (W 'GroupName' 1).Current.Name -eq "Squad $tag" } 10)

  "=== back to the chat"
  GoBack
  Check 'chat shows the system lines' (WHas "удалил(а) Carol Gr" 10)
  Check 'chat header has the new name' (Until { (W 'ChatTitle' 1).Current.Name -eq "Squad $tag" } 5)

  "=== leave"
  WClick 'ChatGroupInfo'; $null=W 'GroupLeave' 10
  WClick 'GroupLeave'; WClickName 'Выйти'
  Check 'back on the chat list' (Until { [bool](W 'MainNewChat' 1) -and -not (W 'GroupLeave' 1) } 10)
  Check 'group gone from the list' (Until { -not (Item "Squad $tag") } 10)
  Check 'server: bob owns it now' (Until { (ChatsOf $b | Where-Object { $_.id -eq $group.id }).ownerId -eq $b.user.id } 10)

  "=== removed by someone else while reading"
  $g2=Api $b Post '/api/chats/group' @{title="Other $tag"; memberIds=@($a.user.id)}
  WSelectItem "Other $tag"
  Check 'second group opens' (Until { (W 'ChatTitle' 1).Current.Name -eq "Other $tag" } 10)
  $null=Api $b Delete "/api/chats/$($g2.id)/members/$($a.user.id)"
  Check 'pane closes' (WHas 'Выберите чат' 10)
  Check 'list drops it' (Until { -not (Item "Other $tag") } 10)

  WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'
  Check 'signed out' ([bool](W 'LoginSubmit'))
} catch { $script:fail++; "ERROR: $_" } finally { if ($proc -and -not $proc.HasExited) { $proc.Kill() } }
Summary

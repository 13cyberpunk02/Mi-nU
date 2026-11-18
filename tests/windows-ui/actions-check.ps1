# What can be done with a message: replying, editing, deleting, reacting, forwarding; "typing"; searching the chat.
$prefix='ac'; . "$PSScriptRoot\common.ps1"

function Last { ChatMessages | Select-Object -Last 1 }
function MessageId($output) { [long](([regex]::Match("$output", 'sent (\d+)')).Groups[1].Value) }
# A tap on a message, as UI Automation does it: selecting its row opens the menu.
function Menu($text) {
  # Only rows of the open chat, the first that has the text: the same text may be a preview in the chat list, or quoted in a later reply.
  $list = W 'ChatMessages'
  $row = $null; $null = Until { $script:row = $list.FindAll($T::Children, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.FindAll($T::Descendants, $All) | Where-Object { $_.Current.Name -eq $text } } | Select-Object -First 1; [bool]$script:row } 8
  if (-not $script:row) { throw "no message $text" }
  $script:row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 1
}
# Whether the open chat has a message with exactly this text.
function InChat($text) { [bool]((W 'ChatMessages' 2).FindAll($T::Descendants, $All) | Where-Object { $_.Current.Name -eq $text } | Select-Object -First 1) }
function Subtitle { @($win.FindAll($T::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like '*печата*' }) }

$a = Register $alice 'Alice Ac'; $b = Register $bob 'Bob Ac'
$chat = Invoke-RestMethod -Method Post -Headers @{ Authorization = "Bearer $($b.token)" } "$api/api/chats/direct" -ContentType 'application/json' -Body (@{ userId = $a.user.id } | ConvertTo-Json)
$group = Api $a Post '/api/chats/group' @{ title = "Fwd $tag"; memberIds = @($b.user.id) }
$question = MessageId (Send "question_$tag needle")
try {
  StartApp
  SignIn $alice
  WSelectItem 'Bob Ac'; $null = W 'ChatInput'
  Check 'bob''s message is there' (WHas "question_$tag" 10)

  "=== reply"
  Menu "question_$tag needle"
  Check 'menu offers a reply' ((SheetItems) -contains 'Ответить')
  Pick 'Ответить'
  Check 'the bar says what is answered' ((W 'ChatContextTitle' 5).Current.Name -eq 'Ответ Bob Ac')
  WSet 'ChatInput' "answer_$tag"; WClick 'ChatSend'
  Check 'server: sent as a reply' (Until { $l = Last; $l.text -eq "answer_$tag" -and $l.replyTo.id -eq $question } 10)
  Check 'the bar is gone' (Until { -not (W 'ChatContextBar' 1) } 3)
  Check 'the reply quotes bob' (WHas "Ответ на сообщение Bob Ac: question_$tag" 5)

  "=== edit"
  Menu "answer_$tag"
  Check 'own message can be edited and deleted' ((SheetItems) -contains 'Изменить')
  Pick 'Изменить'; Start-Sleep 1
  Check 'the field holds the text' ((W 'ChatInput').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq "answer_$tag")
  WSet 'ChatInput' "answer_fixed_$tag"; Start-Sleep 1; WClick 'ChatSend'
  Check 'server: text changed, marked edited' (Until { $l = Last; $l.text -eq "answer_fixed_$tag" -and $l.editedAt } 10)
  Check 'shown as edited' (WHas 'изменено' 5)
  "  bob: $(Send "edit:$question`:question_edited_$tag needle")"
  Check 'bob''s edit reaches alice' (WHas "question_edited_$tag" 10)
  Check 'and the quote of it changes too' (WHas "Ответ на сообщение Bob Ac: question_edited_$tag" 5)

  "=== reactions"
  Menu "question_edited_$tag needle"; Pick 'Реакция'; Start-Sleep 1; Pick '👍'
  Check 'server: alice reacted' (Until { (ChatMessages | Where-Object { $_.id -eq $question }).reactions.emoji -contains '👍' } 10)
  "  bob: $(Send "react:$question`:🔥")"
  Check 'both chips shown, alice''s marked' ((WHas '👍: 1, в том числе вы' 10) -and (WHas '🔥: 1' 5))

  "=== typing"
  $typing = Start-Process -FilePath $sender -ArgumentList $api, $bob, $pw, $chat.id, 'typing:6' -PassThru -WindowStyle Hidden
  Check 'header says bob is typing' (Until { (Subtitle) -contains 'печатает…' } 6)
  $typing.WaitForExit(15000) | Out-Null
  Check 'and stops saying so after' (Until { -not ((Subtitle) -contains 'печатает…') } 10)

  "=== forward"
  Menu "question_edited_$tag needle"; Pick 'Переслать'; Start-Sleep 1; Pick "Fwd $tag"
  Check 'server: in the group, from alice, naming bob' (Until { $l = @(Api $b Get "/api/chats/$($group.id)/messages" | ForEach-Object { $_ }) | Select-Object -Last 1; $l.forwardedFrom -eq 'Bob Ac' -and $l.sender.id -eq $a.user.id } 10)

  "=== search"
  1..3 | ForEach-Object { $null = Send "filler $_" }
  WClick 'ChatSearch'; WSet 'ChatSearchInput' 'NEEDLE'
  # Results come a moment after typing stops; each is a list item described by who wrote what.
  function Hits { @((W 'ChatSearchResults' 1).FindAll($T::Children, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) }
  Check 'finds the message, ignoring case' (Until { (Hits).Count -eq 1 } 10)
  Check 'the result reads as who wrote what' (WHas "Bob Ac: question_edited_$tag needle" 3)
  (Hits)[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Check 'choosing it closes the search' (Until { -not (W 'ChatSearchInput' 1) } 5)
  "=== delete"
  Menu "answer_fixed_$tag"
  # Deleting is the sheet's destructive choice, a button rather than a list item; then the question to confirm.
  WClickName 'Удалить'; Start-Sleep 1; WClickName 'Удалить'
  Check 'server: gone' (Until { -not (ChatMessages | Where-Object { $_.text -eq "answer_fixed_$tag" }) } 10)
  "  bob: $(Send "delete:$question")"
  Check 'bob''s deleted message leaves alice''s chat' (Until { -not (InChat "question_edited_$tag needle") } 10)
} catch { $script:fail++; "ERROR: $_" } finally { StopApp }
Summary

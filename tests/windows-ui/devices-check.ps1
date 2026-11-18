# Choosing the audio output and the camera during a call, with whatever devices this PC has.
$prefix='dv'; . "$PSScriptRoot\common.ps1"

function Sheet($button) { WClick $button; Start-Sleep 2; $script:sheet = @(SheetItems | Where-Object { $_ -ne 'Отмена' }); $script:sheet }
function Current($items) { $items | Where-Object { $_ -like '* (сейчас)' } | ForEach-Object { $_ -replace ' \(сейчас\)$', '' } | Select-Object -First 1 }
function CallOnce($button, $body) {
  $peer = Start-Process -FilePath $peerExe -ArgumentList $api, $bob, $pw, '60' -PassThru -WindowStyle Hidden
  Start-Sleep 3
  WClick $button
  $null = Until { (W 'CallStatus' 1).Current.Name -match '^\d\d:\d\d$' } 25
  try { & $body } finally { if (W 'CallHangUp' 1) { WClick 'CallHangUp' }; Start-Sleep 3; if (-not $peer.HasExited) { $peer.Kill() } }
}

$a = Register $alice 'Alice Dv'; $b = Register $bob 'Bob Dv'
$chat = Invoke-RestMethod -Method Post -Headers @{ Authorization = "Bearer $($b.token)" } "$api/api/chats/direct" -ContentType 'application/json' -Body (@{ userId = $a.user.id } | ConvertTo-Json)
try {
  StartApp
  SignIn $alice
  WSelectItem 'Bob Dv'; $null = W 'ChatInput'

  "=== audio output"
  CallOnce 'ChatCallAudio' {
    Check 'audio output button shown' ([bool](W 'CallAudioOutput' 5))
    $s = Sheet 'CallAudioOutput'; "  outputs: $($s -join ' | ')"
    Check 'the system choice is there and current' ($s -contains 'Как в системе (сейчас)')
    $device = $s | Where-Object { $_ -notlike 'Как в системе*' } | Select-Object -First 1
    if ($device) {
      Pick $device; Start-Sleep 2
      Check 'still in the call after switching the output' ([bool](W 'CallHangUp' 2))
      Check "$device now current" ((Current (Sheet 'CallAudioOutput')) -eq $device)
      Pick 'Как в системе'; Start-Sleep 1
    }
    else { '  only the system output here: switching not checked' }
  }

  "=== cameras"
  # Three cameras or more are offered in a list; with one or two, the button just goes to the next.
  CallOnce 'ChatCallVideo' {
    Check 'switch camera button shown' ([bool](W 'CallSwitchCamera' 5))
    $s = Sheet 'CallSwitchCamera'; "  cameras: $($s -join ' | ')"
    if ($s.Count -ge 3) {
      $script:original = Current $s
      Check 'the camera in use is marked' ([bool]$script:original)
      $script:other = $s | ForEach-Object { $_ -replace ' \(сейчас\)$', '' } | Where-Object { $_ -ne $script:original } | Select-Object -First 1
      Pick $script:other; Start-Sleep 3
      Check "$($script:other) now current" ((Current (Sheet 'CallSwitchCamera')) -eq $script:other)
      Pick 'Отмена'
    }
    else {
      '  one or two cameras: the button switches straight away'
      Start-Sleep 3
      Check 'still in the call after switching' ([bool](W 'CallHangUp' 2))
      Check 'the button is back, ready again' (Until { (W 'CallSwitchCamera' 1).Current.IsEnabled } 10)
      # And back to the first, so the user's own calls aren't left on the other one.
      WClick 'CallSwitchCamera'; Start-Sleep 3
    }
  }
  if ($script:other) {
    CallOnce 'ChatCallVideo' {
      $s = Sheet 'CallSwitchCamera'
      Check 'the next call starts on the camera chosen last' ((Current $s) -eq $script:other)
      # Back to the camera the user had, so their own calls aren't left on another one.
      Pick $script:original; Start-Sleep 3
    }
  }} catch { $script:fail++; "ERROR: $_" } finally { StopApp }
Summary

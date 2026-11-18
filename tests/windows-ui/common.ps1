# Shared by every check: where things are, the test users, and helpers that drive the Windows app through UI Automation.
# A check sets $prefix (two letters, so its users are told apart) and then dot-sources this file.
# Windows PowerShell 5.1; the scripts are saved as UTF-8 with a BOM, which it needs for the Russian text.

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);'
Add-Type -Namespace Dlg -Name W -MemberDefinition (Get-Content "$PSScriptRoot\dialogs.cs" -Raw)
Add-Type -Namespace M -Name C -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(int f,int x,int y,int d,int e);'

# The local server (see run-all.ps1), the app's Debug build, the helper tools, and the files sent in the checks.
$api = if ($env:MINU_TEST_SERVER) { $env:MINU_TEST_SERVER } else { 'http://127.0.0.1:5199' }
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$exe = "$repo\bin\Debug\net11.0-windows10.0.19041.0\win-x64\MinU.exe"
$sender = "$PSScriptRoot\tools\sender\bin\Debug\net11.0\sender.exe"
$peerExe = "$PSScriptRoot\tools\peer\bin\Debug\net11.0-windows10.0.19041.0\win-x64\peer.exe"
$assets = "$PSScriptRoot\assets"
$compose = "$repo\server\docker-compose.yml"
$work = Join-Path $env:TEMP 'minu-ui-checks'
New-Item -ItemType Directory -Force $work | Out-Null
# Where the app keeps its cache when run unpackaged from the Debug folder.
$appCache = "$env:LOCALAPPDATA\User Name\com.cyberpunk02.minu\Cache"

# Fresh users each run, so checks never see each other's chats.
$tag = Get-Random -Minimum 100000 -Maximum 999999
$pw = "Passw0rd_$tag"
$alice = "${prefix}_alice_$tag"
$bob = "${prefix}_bob_$tag"

$script:pass = 0; $script:fail = 0
function Check($l, $c) { if ($c) { $script:pass++; "PASS  $l" } else { $script:fail++; "FAIL  $l" } }
function Until($sb, $sec = 20) { $t0 = Get-Date; do { $r = & $sb; if ($r) { return $true }; Start-Sleep -Milliseconds 700 } while (((Get-Date) - $t0).TotalSeconds -lt $sec); $false }

# UI Automation over the app's window, $win, which each check finds after starting the app.
$WA = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$PC = 'System.Windows.Automation.PropertyCondition'; $All = [System.Windows.Automation.Condition]::TrueCondition
function W($id, $tries = 12) { for ($i = 0; $i -lt $tries; $i++) { $e = $win.FindFirst($T::Descendants, (New-Object $PC($WA::AutomationIdProperty, $id))); if ($e) { return $e }; Start-Sleep 1 }; $null }
function WClick($id) { $e = W $id; if (-not $e) { throw "windows: no $id" }; $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function WSet($id, $text) { (W $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
# Typed key by key, for a password box, which takes no value set from outside.
function WKeys($id, $text) { [W.U]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null; (W $id).SetFocus(); Start-Sleep -Milliseconds 400; foreach ($c in $text.ToCharArray()) { if ([W.U]::GetForegroundWindow() -ne [IntPtr]$hwnd) { throw 'windows: lost focus, stopped typing' }; [System.Windows.Forms.SendKeys]::SendWait("{$c}"); Start-Sleep -Milliseconds 40 } }
function WHas($text, $sec = 20) { Until { foreach ($e in $win.FindAll($T::Descendants, $All)) { if ($e.Current.Name -like "*$text*") { return $true } }; $false } $sec }
function Item($text) { foreach ($e in $win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) { if ($e.FindAll($T::Descendants, $All) | Where-Object { $_.Current.Name -like "*$text*" }) { return $e } }; $null }
function ItemNames($text) { $i = Item $text; if ($i) { @($i.FindAll($T::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) } else { @() } }
function WSelectItem($text) { $null = Until { $script:it = Item $text; [bool]$script:it }; if (-not $it) { throw "windows: no item $text" }; $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function WClickName($name) { $null = Until { $script:btn = $null; foreach ($e in $win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { if ($e.Current.Name -eq $name) { $script:btn = $e; return $true } }; $false } 8; if (-not $btn) { throw "windows: no button $name" }; $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Buttons { @($win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | ForEach-Object { "$($_.Current.AutomationId)/$($_.Current.Name)" }) }
function GoBack { $bb = $win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.AutomationId -like '*Back*' -or $_.Current.Name -in 'Back', 'Назад' } | Select-Object -First 1; if (-not $bb) { throw "no back button: $((Buttons) -join ', ')" }; $bb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
# An action sheet's choice: on Windows its items are list items, not buttons.
function SheetItems { @($win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.FindAll($T::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 1 }) }
function Pick($name) { $item = $null; $null = Until { foreach ($e in $win.FindAll($T::Descendants, (New-Object $PC($WA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) { if ($e.FindAll($T::Descendants, $All) | Where-Object { $_.Current.Name -eq $name }) { $script:item = $e; return $true } }; $false } 8; if (-not $script:item) { throw "no sheet item $name" }; try { $script:item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { $script:item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } }
# A real mouse click in the middle of an element, for views that only answer taps.
function ClickAt($rect, $dx = $null, $dy = $null) { $x = if ($null -ne $dx) { $rect.X + $dx } else { $rect.X + $rect.Width / 2 }; $y = if ($null -ne $dy) { $rect.Y + $dy } else { $rect.Y + $rect.Height / 2 }; [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$x, [int]$y); [M.C]::mouse_event(2, 0, 0, 0, 0); [M.C]::mouse_event(4, 0, 0, 0, 0) }

# The system file dialog: the file name box, then Enter. On Windows 11 the picker runs in its own process, PickerHost.
function PickFile($path) {
  $script:handle = [IntPtr]::Zero; $null = Until { foreach ($ph in Get-Process PickerHost -ErrorAction SilentlyContinue) { $h = [Dlg.W]::Dialog($ph.Id); if ($h -ne [IntPtr]::Zero) { $script:handle = $h; return $true } }; $false } 30
  if ($script:handle -eq [IntPtr]::Zero) { throw 'no file dialog' }
  # Typed from a plain folder: SendKeys can't type every character in every keyboard layout, the backtick in Mi`nU among them.
  $plain = Join-Path $work (Split-Path $path -Leaf)
  Copy-Item -LiteralPath $path -Destination $plain -Force
  # The dialog opens with the file name box focused; typing there is what a person does.
  [W.U]::SetForegroundWindow($script:handle) | Out-Null; Start-Sleep -Milliseconds 800
  [System.Windows.Forms.SendKeys]::SendWait($plain); Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

# The server, as Bob and Alice see it.
function Register($u, $d) { Invoke-RestMethod -Method Post "$api/api/auth/register" -ContentType 'application/json' -Body (@{ userName = $u; displayName = $d; password = $pw } | ConvertTo-Json) }
function Api($auth, $method, $path, $body) { $p = @{ Method = $method; Uri = "$api$path"; Headers = @{ Authorization = "Bearer $($auth.token)" } }; if ($body) { $p.ContentType = 'application/json'; $p.Body = ($body | ConvertTo-Json -Compress) }; Invoke-RestMethod @p }
function ChatsOf($auth) { @(Api $auth Get '/api/chats' | ForEach-Object { $_ }) }
function ChatAs($auth) { @(Invoke-RestMethod -Headers @{ Authorization = "Bearer $($auth.token)" } "$api/api/chats" | ForEach-Object { $_ }) | Where-Object { $_.id -eq $chat.id } }
function ChatMessages { @(Invoke-RestMethod -Headers @{ Authorization = "Bearer $($b.token)" } "$api/api/chats/$($chat.id)/messages" | ForEach-Object { $_ }) }
# Bob acting through SignalR as the app does; see tools/sender.
function Send($text) { & $sender $api $bob $pw $chat.id $text 2>&1 }
# The local database, for what no API does, such as moving a message back in time.
function Psql($sql) { $sql | docker compose -f $compose exec -T postgres psql -U minu -d minu -t -A 2>&1 }
function JpegSize($bytes) { $i = 2; while ($i -lt $bytes.Length - 9) { if ($bytes[$i] -ne 0xFF) { return $null }; $m = $bytes[$i + 1]; $len = $bytes[$i + 2] * 256 + $bytes[$i + 3]; if ($m -ge 0xC0 -and $m -le 0xCF -and $m -notin 0xC4, 0xC8, 0xCC) { return "$($bytes[$i + 7] * 256 + $bytes[$i + 8])x$($bytes[$i + 5] * 256 + $bytes[$i + 6])" }; $i += 2 + $len }; $null }

# Starts the app from its Debug build and finds its window; any copy left running from before goes first.
function StartApp {
  Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
  Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$exe`"" | Out-Null
  $script:proc = $null
  $null = Until { $script:proc = Get-Process MinU -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1; [bool]$script:proc } 30
  if (-not $script:proc) { throw 'the app did not start' }
  Start-Sleep 4
  $script:hwnd = $script:proc.MainWindowHandle
  $script:win = $WA::RootElement.FindFirst($T::Children, (New-Object $PC($WA::ProcessIdProperty, $script:proc.Id)))
  $script:win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1200, 850)
  [W.U]::SetForegroundWindow([IntPtr]$script:hwnd) | Out-Null
}

# Signs in as $user, first signing out whoever the app remembered.
function SignIn($user) {
  if (W 'MainProfile' 3) { WClick 'MainProfile'; Start-Sleep 2; WClickName 'Выйти'; $null = W 'LoginSubmit' }
  WSet 'LoginServer' $api; WSet 'LoginUserName' $user; WKeys 'LoginPassword' $pw; WClick 'LoginSubmit'
  Check 'signed in' ([bool](W 'MainNewChat'))
}

function StopApp { if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() }; Get-Process PickerHost, OpenWith -ErrorAction SilentlyContinue | Stop-Process -Force }

function Summary { "=== $($script:pass) passed, $($script:fail) failed"; if ($script:fail -gt 0) { exit 1 } }

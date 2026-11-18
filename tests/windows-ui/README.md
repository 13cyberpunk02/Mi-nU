**English** | [Русский](README.ru.md)

# Windows UI checks

End-to-end checks of the Windows app: each one starts the app's Debug build, signs in as a fresh test user, and drives it
through UI Automation while a second test user acts on the server — sending messages, photos, voice and video messages,
calling. They catch what unit tests can't: that the screens still work together after a change.

## Running

```powershell
powershell -ExecutionPolicy Bypass -File tests\windows-ui\run-all.ps1
```

This builds the app and the two helper tools, starts the database and file storage from `server\docker-compose.yml`, starts the
server on `http://127.0.0.1:5199` in Development (unless one already answers there), runs every check and prints one line each.
It exits with 1 if any check failed.

- `-NoBuild` skips building, when the app is already built.
- `-Only voice-check,layout-check` runs only those.
- One check on its own: `powershell -ExecutionPolicy Bypass -File tests\windows-ui\voice-check.ps1` (the server must be running).

Needs Docker Desktop, the .NET 11 SDK, and `ffprobe` (part of FFmpeg) for the recording checks.
A full run takes about half an hour. **Leave the desktop alone meanwhile:** the checks click and type, open the file dialog,
and record a few seconds from the microphone and the camera — those recordings go only to the local server.

## What's here

| File | |
|---|---|
| `common.ps1` | Paths, test users and the helpers every check uses |
| `*-check.ps1`, `photo-hang.ps1` | The checks, one feature each; the first line says what |
| `run-all.ps1` | Runs them all |
| `tools/sender` | The other person in a chat: sends messages, files, voice and video messages, calls, answers |
| `tools/peer` | A real call participant through 13CyberPunk02.Maui.WebRTC, for checks that need sound and video |
| `assets/` | What the checks send: a 3000×2000 photo with a made-up EXIF marker, a picture, a voice and a video message |
| `dialogs.cs` | Finds the system file dialog, which on Windows 11 runs in its own process |

## Writing a check

Start with a comment saying what it checks, then `$prefix='xx'; . "$PSScriptRoot\common.ps1"` with two letters of its own.
Find elements by `AutomationId`, report with `Check 'what' (condition)`, wait with `Until { ... } seconds`, and end with
`Summary`. Save it as UTF-8 **with a BOM**: Windows PowerShell 5.1 reads the Russian text wrong otherwise.

**English** | [Русский](README.ru.md)

# Mi`nU

A messenger for Android and Windows, built with .NET MAUI and an ASP.NET Core server. Text chats go through SignalR, and voice and video calls use [13CyberPunk02.Maui.WebRTC](https://www.nuget.org/packages/13CyberPunk02.Maui.WebRTC).

> Status: early development. Calls only connect when devices can reach each other directly, such as on one local network: there are no STUN or TURN servers yet.

## Features

- Sign-up and sign-in with a username and password
- Direct and group chats, and searching for people by username or name
- Live message delivery and paged history
- Retrying a failed message without creating a duplicate
- One-to-one voice and video calls with an incoming call screen, while the app is open
- Group calls of up to 4 people, with every device connected directly to the others

### Planned

- Calls across the internet
- Incoming calls and notifications while the app is closed
- Unread counters and group management

## Platforms

| Platform | Requirements |
|---|---|
| Android | 7.0 (API 24) or later, arm64 only: x86_64 emulators are not supported |
| Windows | Windows 10 1809 or later, x64 |

## Getting started

You need the .NET 10 SDK, the .NET MAUI workloads and Docker.

Run these commands from the repository root. The first starts PostgreSQL for development, and the second starts the server, which applies database migrations in the `Development` environment:

```powershell
docker compose -f server/docker-compose.yml up -d
dotnet run --project server/MinU.Server --urls http://0.0.0.0:5199
```

Then start the app from Visual Studio, or from another terminal:

```powershell
dotnet build 'Mi`nU.csproj' -t:Run -f net10.0-windows10.0.19041.0
```

Keep the single quotes: in PowerShell, the backtick is an escape character.

### Connecting a phone

Debug builds connect to `http://127.0.0.1:5199` by default, and the sign-in screen has a field for changing the address.

- **Over USB**, forward the port:
  ```powershell
  adb reverse tcp:5199 tcp:5199
  ```
- **Over Wi-Fi**, enter your computer's address, such as `http://192.168.0.80:5199`, and allow port 5199 through the firewall.

## Project structure

| Path | Purpose |
|---|---|
| `Pages/`, `MainPage.xaml` | App screens |
| `Services/` | Sign-in, the HTTP API client and the SignalR client |
| `server/MinU.Server` | ASP.NET Core server: REST API, SignalR hub and database |
| `server/MinU.Contracts` | Types shared by the app and the server |
| `server/MinU.Server.Tests` | Server tests |

## Tests

The tests need the PostgreSQL container from above, and each run creates and deletes its own database:

```powershell
dotnet test server/MinU.Server.Tests
```

## Server

Deploying the server with PostgreSQL, HTTPS, TURN and push notifications is covered in [`server/deploy/README.md`](server/deploy/README.md).

## Release builds

Release builds have no server address field: the address is set when building, and the build stops without it.

### Android

The APK is signed with your own key. Create it once:

```powershell
keytool -genkeypair -v -keystore D:\keys\minu.keystore -alias minu -keyalg RSA -keysize 4096 -validity 10000
```

`keytool` comes with the JDK, for example `C:\Program Files\Microsoft\jdk-…\bin\keytool.exe`. Keep the key and its
password out of the repository and back them up: an update only installs over an app signed with the same key, and a
lost key can't be recovered.

Build:

```powershell
$env:MINU_KEYSTORE = "D:\keys\minu.keystore"
$env:MINU_KEYSTORE_PASSWORD = "the key's password"
dotnet publish 'Mi`nU.csproj' -c Release -f net10.0-android -p:MinuServerUrl=https://api.example.com -o publish/android
```

The result is `publish/android/com.cyberpunk02.minu-Signed.apk`. The phone asks to allow installing from that source.
An app installed by a debug build is signed with a different key: uninstall it before the first release install.

### Windows

```powershell
dotnet publish 'Mi`nU.csproj' -c Release -f net10.0-windows10.0.19041.0 -p:MinuServerUrl=https://api.example.com -o publish/windows
```

`publish/windows` holds everything, .NET and Windows App SDK included: zip it and hand it over, nothing needs installing
on the other computer. Start `MinU.exe`. Notifications don't work when it runs as administrator.

### Trying a release build against a local server

`-p:MinuAllowCleartext=true` allows plain HTTP, to try a release build against a server on your own computer. Never hand
such a build out.

## Before deploying

- Run a single server instance: calls in progress are kept in memory.
- Push notifications are Android only. On Windows, notifications arrive while the app is running.

## License

[MIT](LICENSE.txt) © 13CyberPunk02

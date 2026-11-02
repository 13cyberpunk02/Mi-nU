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

## Before deploying

The server and app are not ready for production as they are:

- Set `Jwt:Key` to your own secret of at least 32 characters, and `ConnectionStrings:Default` to your database. Don't use the values from `appsettings.Development.json`.
- Set `Database:MigrateOnStartup` to `true` or apply migrations yourself: outside `Development` they don't run by default.
- Use HTTPS, and set the server address in `AuthService`: release builds have no address field, and Android 9 and later block plain HTTP in them.
- Run a single server instance: calls in progress are kept in memory.

## License

[MIT](LICENSE.txt) © 13CyberPunk02

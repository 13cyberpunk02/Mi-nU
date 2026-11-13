**English** | [Русский](README.ru.md)

# Deploying the Mi`nU server

This runs the Mi`nU server (API, SignalR for chats and call signaling, PostgreSQL) on a Linux machine behind
[Caddy](https://caddyserver.com/), which gets and renews the HTTPS certificate on its own. Calls over the internet also
need a TURN server: set it up with [the Maui.WebRTC guide](https://github.com/13cyberpunk02/Maui.WebRTC/blob/main/docs/turn.md),
on this machine or another one.

```
App ──HTTPS/WSS──► Caddy :443 ──► MinU.Server :8080 ──► PostgreSQL
 │
 └── audio and video ──► coturn :3478/:5349 (only when devices can't reach each other directly)
```

## 1. What you need

- A Linux server with a public IPv4 address and Docker (`curl -fsSL https://get.docker.com | sudo sh`).
- A domain for the API, such as `api.example.com`, with an A record pointing at the server. The TURN server's domain
  works too: Caddy uses 443, coturn 3478 and 5349.
- A working TURN server and its `static-auth-secret`.

## 2. Firewall

```bash
sudo ufw allow 80/tcp     # getting and renewing the certificate
sudo ufw allow 443/tcp    # API and SignalR
sudo ufw allow 443/udp    # HTTP/3, optional
```

The server's port 8080 and PostgreSQL's port stay closed: only Caddy reaches the server.

## 3. Code and settings

```bash
sudo git clone https://github.com/13cyberpunk02/Mi-nU.git /opt/minu
cd /opt/minu/server/deploy
sudo cp .env.example .env
sudo chmod 600 .env
```

For a private repository, `git clone` asks for a username and a [GitHub access token](https://github.com/settings/tokens)
instead of a password.

Fill in `.env`:

| Setting | Value |
|---|---|
| `API_DOMAIN` | the API domain, such as `api.example.com` |
| `POSTGRES_PASSWORD` | `openssl rand -hex 32` |
| `JWT_KEY` | `openssl rand -hex 32`, the key that signs sign-in tokens |
| `TURN_HOST` | the TURN server's domain |
| `TURN_SECRET` | `static-auth-secret` from `turnserver.conf` |

Don't change `JWT_KEY` without reason: everyone would have to sign in again. `.env` is kept out of git.

### Firebase key for push notifications

In the [Firebase console](https://console.firebase.google.com), open your project, then **Project settings → Service
accounts → Generate new private key**. Copy the downloaded file to the server as `firebase.json`:

```bash
# from your computer:
scp path/to/downloaded-key.json user@your-server:/tmp/firebase.json
# on the server:
sudo mv /tmp/firebase.json /opt/minu/server/deploy/firebase.json
sudo chown 1654:1654 /opt/minu/server/deploy/firebase.json
sudo chmod 600 /opt/minu/server/deploy/firebase.json
```

The server runs as uid `1654` in its container, so the file must belong to that user, or the server can't read the key
and runs without notifications. The key lets anyone send notifications as your app: keep it out of the repository and
don't pass it around. `firebase.json` is kept out of git.

## 4. Start

```bash
cd /opt/minu/server/deploy
sudo docker compose up -d --build
sudo docker compose ps
sudo docker compose logs -f server caddy
```

The first image build takes a few minutes. On its first start the server creates the database tables itself. The
`server` log then has one `fail` line about `__EFMigrationsHistory`: that's EF checking whether the migrations table
exists yet, not an error. Caddy's log says when it got the certificate for `API_DOMAIN`.

The `server` log should have no `push notifications are off` line. If it has, the server didn't find or couldn't read
`firebase.json`: check the file's owner from step 3.

## 5. Check

From any computer:

```bash
curl -i https://api.example.com/api/chats
```

The expected answer is `401 Unauthorized`: the server runs, the certificate is valid, and nothing is served without
signing in.

Then the full check:

```bash
TOKEN=$(curl -s https://api.example.com/api/auth/register -H 'Content-Type: application/json' \
  -d '{"userName":"check_user","displayName":"Check","password":"check-password-1"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')
curl -s https://api.example.com/api/ice-servers -H "Authorization: Bearer $TOKEN"
```

The second answer should hold four `stun:`, `turn:` and `turns:` addresses with your `TURN_HOST`, and a temporary
`username` and `credential`. Remove the user afterwards:

```bash
sudo docker compose exec postgres psql -U minu -d minu -c "DELETE FROM \"Users\" WHERE \"UserName\" = 'check_user';"
```

## 6. The app

Debug builds have a "Server address" field on the sign-in screen. Enter `https://api.example.com` and sign up on two
devices. A call between a phone on mobile data and a computer on Wi-Fi goes through TURN.

Release builds take the address from the code (`DefaultServerUrl` in `Services/AuthService.cs`) and have no field for it.

## 7. When TURN runs on the same machine

coturn's certificate is renewed by `certbot --standalone`, which needs port 80, and Caddy holds it. Let certbot stop
Caddy while it renews. Add to `/etc/letsencrypt/renewal/<TURN domain>.conf`, under `[renewalparams]`:

```ini
pre_hook = docker compose -f /opt/minu/server/deploy/docker-compose.yml stop caddy
post_hook = docker compose -f /opt/minu/server/deploy/docker-compose.yml start caddy
```

And check:

```bash
sudo certbot renew --dry-run
```

The API is unreachable while it renews, usually a few seconds every two months.

## 8. Updating

```bash
cd /opt/minu
sudo git pull
cd server/deploy
sudo docker compose up -d --build
```

New database migrations are applied on start. PostgreSQL's data lives in the `postgres` volume and survives updates.

## 9. Database backup

```bash
sudo docker compose exec postgres pg_dump -U minu minu | gzip > minu-$(date +%F).sql.gz
```

Keep the copies somewhere other than this server.

## 10. Limitations

- Run a single server instance: calls in progress are kept in its memory.
- Push notifications are Android only; on Windows an incoming call arrives while the app is running.

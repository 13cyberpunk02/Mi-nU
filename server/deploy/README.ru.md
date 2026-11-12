[English](README.md) | **Русский**

# Развёртывание сервера Mi`nU

Здесь сервер Mi`nU (API, SignalR для чатов и сигнализации звонков, PostgreSQL) разворачивается на Linux-сервере за
[Caddy](https://caddyserver.com/), который сам получает и продлевает HTTPS-сертификат. Звонкам через интернет нужен ещё
TURN-сервер: его разворачивают по [гайду из Maui.WebRTC](https://github.com/13cyberpunk02/Maui.WebRTC/blob/main/docs/turn.ru.md),
можно на этой же машине.

```
Приложение ──HTTPS/WSS──► Caddy :443 ──► MinU.Server :8080 ──► PostgreSQL
     │
     └── звук и видео ──► coturn :3478/:5349 (только если устройства не видят друг друга напрямую)
```

## 1. Что понадобится

- Linux-сервер с публичным IPv4 и Docker (`curl -fsSL https://get.docker.com | sudo sh`).
- Домен для API, например `api.example.com`, с A-записью на адрес сервера. Можно взять и тот же домен, что у TURN:
  Caddy работает на 443, а coturn на 3478 и 5349.
- Уже работающий TURN-сервер и его `static-auth-secret`.

## 2. Брандмауэр

```bash
sudo ufw allow 80/tcp     # получение и продление сертификата
sudo ufw allow 443/tcp    # API и SignalR
sudo ufw allow 443/udp    # HTTP/3, необязательно
```

Порт 8080 сервера и порт PostgreSQL наружу не открываются: до сервера доходит только Caddy.

## 3. Код и настройки

```bash
sudo git clone https://github.com/13cyberpunk02/Mi-nU.git /opt/minu
cd /opt/minu/server/deploy
sudo cp .env.example .env
sudo chmod 600 .env
```

Если репозиторий закрытый, `git clone` попросит логин и [токен доступа GitHub](https://github.com/settings/tokens)
вместо пароля.

Заполните `.env`:

| Параметр | Значение |
|---|---|
| `API_DOMAIN` | домен API, например `api.example.com` |
| `POSTGRES_PASSWORD` | `openssl rand -hex 32` |
| `JWT_KEY` | `openssl rand -hex 32`, ключ подписи токенов входа |
| `TURN_HOST` | домен TURN-сервера |
| `TURN_SECRET` | `static-auth-secret` из `turnserver.conf` |

`JWT_KEY` не меняйте без нужды: после смены всем придётся войти заново. `.env` в git не попадает.

## 4. Запуск

```bash
cd /opt/minu/server/deploy
sudo docker compose up -d --build
sudo docker compose ps
sudo docker compose logs -f server caddy
```

Первая сборка образа занимает несколько минут. При первом запуске сервер сам создаст таблицы в базе. В журнале
`server` будет одна строка `fail` про `__EFMigrationsHistory`: так EF проверяет, есть ли уже таблица миграций, это не
ошибка. Caddy в журнале сообщит, что получил сертификат для `API_DOMAIN`.

## 5. Проверка

С любого компьютера:

```bash
curl -i https://api.example.com/api/chats
```

Ожидаемый ответ — `401 Unauthorized`: сервер работает, сертификат действителен, а без входа данные не отдаются.

Затем полная проверка:

```bash
TOKEN=$(curl -s https://api.example.com/api/auth/register -H 'Content-Type: application/json' \
  -d '{"userName":"check_user","displayName":"Check","password":"check-password-1"}' | sed -E 's/.*"token":"([^"]+)".*/\1/')
curl -s https://api.example.com/api/ice-servers -H "Authorization: Bearer $TOKEN"
```

Второй ответ должен содержать четыре адреса `stun:`, `turn:` и `turns:` с вашим `TURN_HOST`, а также временные
`username` и `credential`. После проверки удалите пользователя:

```bash
sudo docker compose exec postgres psql -U minu -d minu -c "DELETE FROM \"Users\" WHERE \"UserName\" = 'check_user';"
```

## 6. Приложение

В отладочной сборке на экране входа есть поле «Адрес сервера». Введите `https://api.example.com` и зарегистрируйтесь
на двух устройствах. Звонок между телефоном в мобильной сети и компьютером в Wi-Fi пойдёт через TURN.

Для релизной сборки адрес задаётся в коде (`DefaultServerUrl` в `Services/AuthService.cs`), поля для него в релизе нет.

## 7. Если TURN и сервер на одной машине

coturn продлевает сертификат через `certbot --standalone`, которому нужен порт 80, а его занимает Caddy. Пусть
certbot на время продления останавливает Caddy. Добавьте в `/etc/letsencrypt/renewal/<домен TURN>.conf`, в раздел
`[renewalparams]`:

```ini
pre_hook = docker compose -f /opt/minu/server/deploy/docker-compose.yml stop caddy
post_hook = docker compose -f /opt/minu/server/deploy/docker-compose.yml start caddy
```

И проверьте:

```bash
sudo certbot renew --dry-run
```

На время продления, обычно несколько секунд раз в два месяца, API будет недоступен.

## 8. Обновление

```bash
cd /opt/minu
sudo git pull
cd server/deploy
sudo docker compose up -d --build
```

Новые миграции базы применятся при запуске. Данные PostgreSQL хранятся в томе `postgres` и при обновлении не теряются.

## 9. Резервная копия базы

```bash
sudo docker compose exec postgres pg_dump -U minu minu | gzip > minu-$(date +%F).sql.gz
```

Храните копии не на этом же сервере.

## 10. Ограничения

- Запускайте сервер в одном экземпляре: текущие звонки хранятся в его памяти.
- Push-уведомлений нет: входящий звонок приходит, только пока приложение открыто.

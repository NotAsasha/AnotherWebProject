# Локальне середовище

## Перший запуск Docker

1. У PowerShell адміністратора: `wsl --install`. Перезавантажте Windows за потреби.
2. Встановіть [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)
   із WSL 2 backend; запустіть у режимі Linux containers.
3. У новому PowerShell перевірте `docker info` і `docker compose version`.
4. У корені репозиторію, якщо `.env` ще немає:

```powershell
Copy-Item .env.example .env
notepad .env
```

Задайте різні паролі PostgreSQL і RabbitMQ: від 20 латинських літер/цифр.
Це нові облікові дані, які контейнери створять при першій ініціалізації.
`.env` виключений із Git. Зміна `.env` не змінює пароль у вже ініціалізованому томі.

## Варіант A: backend у Docker

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Configure-Local.ps1
docker compose config --quiet
docker compose --profile app up -d --build --wait --wait-timeout 180
docker compose ps
```

Скрипт генерує випадковий JWT_SIGNING_KEY у `.env`, якщо його ще немає.
Compose застосовує EF migrations окремим сервісом `migrate` перед API/Worker;
його стан `Exited (0)` після успішного завершення — очікуваний.
Запускаються PostgreSQL, Redis, RabbitMQ, API, Worker, YARP і Vue-клієнт.
Відкрийте http://localhost:5173, зареєструйтеся та запустіть симуляцію.

| Сервіс | Адреса |
|---|---|
| Інтерфейс | http://localhost:5173 |
| YARP | http://localhost:5297/health/ready |
| API | http://localhost:5179/health/ready |
| Worker | http://localhost:5181/health/ready |
| PostgreSQL | localhost:5433, БД contaminantmigration, користувач contaminant |
| Redis | localhost:6379 |
| RabbitMQ | localhost:5672, користувач contaminant |
| RabbitMQ Management | http://localhost:15672 |

`/health/live` перевіряє процес; `/health/ready` — залежності.
У контейнерах PostgreSQL доступний як postgres:5432.
Порти на Windows прив'язані до 127.0.0.1. Це конфігурація локальної розробки.
Режими реплік, автомасштабування та локального HTTPS описані в README.

## Варіант B: backend з IDE або термінала

Якщо варіант A уже запущений, звільніть порти застосунків:

```powershell
docker compose --profile app stop client api worker gateway
```

Підніміть інфраструктуру й створіть локальні налаштування з даних Compose:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Configure-Local.ps1
docker compose up -d --wait --wait-timeout 180
dotnet tool restore
dotnet ef database update --project src/ContaminantMigration.Infrastructure --startup-project src/ContaminantMigration.Infrastructure
```

Скрипт записує лише локальні параметри підключень до `appsettings.Local.json`
у WebApi/Worker; файли виключені з Git і Docker build context.
Змінні оточення та аргументи командного рядка мають вищий пріоритет.
Паролі не виводяться. Після зміни портів/паролів виконайте скрипт повторно.

У трьох окремих терміналах:

```powershell
dotnet run --project src/ContaminantMigration.WebApi --launch-profile http
dotnet run --project src/ContaminantMigration.Worker
dotnet run --project src/ContaminantMigration.LoadBalancer --launch-profile http
```

Для frontend в окремому терміналі: `npm.cmd --prefix client run dev`.

## Зупинка і діагностика

```powershell
docker compose --profile app logs --tail 50 api worker gateway
docker compose --profile app stop
```

Томи з даними зберігаються. Не використовуйте `down --volumes` для звичайної зупинки.
При конфлікті порту змініть лівий порт у Compose. PostgreSQL уже використовує
5433 замість 5432 через попередню помилку прив'язки Windows.

MassTransit зафіксовано на 8.5.12; FluentValidation — DI-інтеграція 12.2.0.
Major-теги Docker-образів дозволяють отримувати виправлення; digest фіксується
окремо для відтворюваного релізного розгортання.

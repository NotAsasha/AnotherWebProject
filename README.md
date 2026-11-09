# ContaminantMigration

Моделювання 2D-перенесення та дифузії домішки.
.NET 10, Vue 3 / TypeScript, PostgreSQL, Redis, RabbitMQ, MassTransit, YARP.

Готовий інфраструктурний каркас: підключення сервісів, health checks, Dockerfile,
Compose та проксі. Чисельний solver, авторизація й задачі — наступні етапи.

- [Специфікація моделі та API](docs/01-specification.md).
- [Docker і локальний запуск](docs/02-docker-setup.md).

Після створення `.env` з власними паролями:

```powershell
docker compose --profile app up -d --build --wait --wait-timeout 180
```

Перевірка через YARP: http://localhost:5297/health/ready.
API: http://localhost:5179/health/ready. Worker: http://localhost:5181/health/ready.

Збірка без контейнерів:

```powershell
dotnet build ContaminantMigration.slnx
npm.cmd --prefix client run build
```

# ContaminantMigration

Моделювання 2D-перенесення та дифузії домішки.
.NET 10, Vue 3 / TypeScript, PostgreSQL, Redis, RabbitMQ, MassTransit, YARP.

Реалізовано інфраструктурний каркас і чисельне ядро FDM + Upwind у Core:
валідація стійкості й лімітів, Float32-кадри, прогрес, скасування та тайм-аут.
Авторизація, збереження задач і підключення solver до Worker — наступні етапи.

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

Перевірка чисельного ядра (без Docker):

```powershell
dotnet test tests/ContaminantMigration.Core.Tests -c Release
```

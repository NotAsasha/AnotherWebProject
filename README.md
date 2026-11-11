# ContaminantMigration

Моделювання 2D-перенесення та дифузії домішки.
.NET 10, Vue 3 / TypeScript, PostgreSQL, Redis, RabbitMQ, MassTransit, YARP.

Реалізовано інфраструктурний каркас і чисельне ядро FDM + Upwind у Core:
валідація стійкості й лімітів, Float32-кадри, прогрес, скасування та тайм-аут.
Додано PostgreSQL-схему й JWT-авторизацію. Запуск задач через чергу та підключення
solver до Worker — наступний етап.

- [Специфікація моделі та API](docs/01-specification.md).
- [Docker і локальний запуск](docs/02-docker-setup.md).

Після створення `.env` з власними паролями:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Configure-Local.ps1
docker compose --profile app up -d --build --wait --wait-timeout 180
```

Перевірка через YARP: http://localhost:5297/health/ready.
API: http://localhost:5179/health/ready. Worker: http://localhost:5181/health/ready.

Збірка без контейнерів:

```powershell
dotnet build ContaminantMigration.slnx
npm.cmd --prefix client run build
```

Тести чисельного ядра й авторизації (без Docker):

```powershell
dotnet test ContaminantMigration.slnx -c Release
```

Авторизація: `POST /api/v1/auth/register`, `POST /api/v1/auth/login`,
`GET /api/v1/auth/me` (заголовок `Authorization: Bearer <accessToken>`).
Реєстрація/вхід приймають JSON `{ "email": "...", "password": "..." }`.
Пароль — 12–128 символів; токен чинний 30 хвилин. Нові користувачі мають роль User.

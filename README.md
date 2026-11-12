# ContaminantMigration

Моделювання 2D-перенесення та дифузії домішки.
.NET 10, Vue 3 / TypeScript, PostgreSQL, Redis, RabbitMQ, MassTransit, YARP.

Реалізовано інфраструктурний каркас і чисельне ядро FDM + Upwind у Core:
валідація стійкості й лімітів, Float32-кадри, прогрес, скасування та тайм-аут.
Додано PostgreSQL-схему, JWT-авторизацію та виконання задач через RabbitMQ:
transactional outbox, квоти, idempotency, скасування, прогрес і відновлення Worker.
Vue-клієнт: реєстрація, вхід, параметри, історія, SignalR-прогрес і Canvas-програвач
кадрів. При втраті SignalR стан оновлюється через REST.

- [Специфікація моделі та API](docs/01-specification.md).
- [Docker і локальний запуск](docs/02-docker-setup.md).

Після створення `.env` з власними паролями:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Configure-Local.ps1
docker compose --profile app up -d --build --wait --wait-timeout 180
```

Інтерфейс: http://localhost:5173. Перевірка через YARP: http://localhost:5297/health/ready.
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

Задачі (потрібен Bearer token):

```text
GET    /api/v1/simulation-limits
POST   /api/v1/simulations/validate
POST   /api/v1/simulations                    -> 202 + id; Idempotency-Key: UUID (необов'язково)
GET    /api/v1/simulations?page=1&pageSize=20
GET    /api/v1/simulations/{id}
POST   /api/v1/simulations/{id}/cancel
GET    /api/v1/simulations/{id}/result
GET    /api/v1/simulations/{id}/frames/{index}
DELETE /api/v1/simulations/{id}
```

Тіло створення/валідації — у [специфікації](docs/01-specification.md).
Worker бере одну задачу; перевіряє скасування кожні 250 мс, поновлює оренду
щонайменше раз на 5 с. Оренда 30 с, до 2 спроб після втрати Worker.
Перевірка інтеграції на локальному Docker:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Test-Simulations.ps1
```

Скрипт тимчасово зупиняє Worker/RabbitMQ та видаляє лише власні тестові записи.

Перевірка навантаження (один Worker, без активних задач):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Test-Load.ps1
```

Три важкі задачі, прогрес, CPU/пам'ять, скасування, аварійне відновлення й тайм-аут.
Скрипт тимчасово перестворює Worker, відновлює звичайну конфігурацію та очищає власні записи.
`-ReportPath <path.json>` зберігає вимірювання; без параметра файл не створюється.
Ліміти API/Worker можна перевизначити через `SimulationLimits__MaxCellUpdates`
і `SimulationLimits__MaxExecutionTime` (наприклад, `00:01:00`).

Два API й два Worker (Docker Compose 2.24.4+):

```powershell
docker compose -f docker-compose.yml -f docker-compose.replicas.yml --profile app up -d --build --wait --wait-timeout 180
npm.cmd --prefix client run test:replicas
```

Інтерфейс і Gateway мають ті самі адреси. API2: `http://localhost:5180`.
Worker отримують вільні локальні порти; `5181` у цьому режимі не фіксований.
REST балансується Round Robin, SignalR має окремий кластер із cookie affinity.
Тест перевіряє паралельне виконання, Redis-доставку й відмову API2; активних задач перед запуском бути не повинно.
Кількість Worker можна змінювати через `--scale worker=N`.
Для повернення до одного API/Worker:

```powershell
docker compose -f docker-compose.yml -f docker-compose.replicas.yml stop api2
docker compose --profile app up -d --scale worker=1 --wait --wait-timeout 180
```

Перевірки клієнта: `npm.cmd --prefix client run test:unit`.
Інтеграція з увімкненим Docker: `npm.cmd --prefix client run test:realtime`.
Браузерні тести запускайте з папки `client`: `npm.cmd run test:e2e` (потрібен Chrome).
Вони створюють і очищають власні записи; тест скасування тимчасово зупиняє Worker.

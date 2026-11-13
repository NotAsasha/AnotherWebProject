[Відкрити сайт](https://localhost:5443)

```powershell
# Перший запуск (.NET 10 SDK і Docker Desktop)

Copy-Item .env.example .env
notepad .env

powershell -ExecutionPolicy Bypass -File scripts/Configure-Local.ps1
powershell -ExecutionPolicy Bypass -File scripts/Configure-Https.ps1 -Trust

docker compose -f docker-compose.yml -f docker-compose.replicas.yml -f docker-compose.autoscale.yml -f docker-compose.https.yml --profile app up -d --build --wait --wait-timeout 180

# Запуск / оновлення після змін коду
docker compose up -d --build --wait --wait-timeout 180

# Зупинка
docker compose stop

# Логи
docker compose logs --tail 50 api api2 worker scaler gateway client

# Призначення адміністратора; після виконання увійти повторно
docker compose exec api dotnet ContaminantMigration.WebApi.dll --Admin:PromoteEmail="user@example.com"

# Тести
dotnet test ContaminantMigration.slnx -c Release
npm.cmd --prefix client run test:unit
```

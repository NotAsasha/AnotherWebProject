FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=ContaminantMigration.WebApi
WORKDIR /source
COPY src/ src/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release --no-restore -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT=ContaminantMigration.WebApi
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /publish .
ENV APP_PROJECT=${PROJECT}
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"${APP_PROJECT}.dll\""]

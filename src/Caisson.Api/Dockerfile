# Caisson.Api production image. Multi-stage: restore+publish on the SDK, run on the smaller aspnet
# runtime as a non-root user. Build context is the repo root (the API references sibling src/ projects),
# so build with:  docker build -f src/Caisson.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Caisson.Api/Caisson.Api.csproj
RUN dotnet publish src/Caisson.Api/Caisson.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# ACA ingress targets 8080; bind Kestrel there explicitly rather than relying on the base image default.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# aspnet:8.0 ships a pre-created non-root user (APP_UID); run as it (no root in the running container).
USER $APP_UID
ENTRYPOINT ["dotnet", "Caisson.Api.dll"]

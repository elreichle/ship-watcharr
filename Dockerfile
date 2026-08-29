# syntax=docker/dockerfile:1

# ---- Stage 1: build the React frontend ----
FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# ---- Stage 2: build & publish the .NET backend ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/Ao3Tracker.Api/Ao3Tracker.Api.csproj ./backend/Ao3Tracker.Api/
RUN dotnet restore ./backend/Ao3Tracker.Api/Ao3Tracker.Api.csproj
COPY backend/ ./backend/
# The compiled frontend becomes the backend's wwwroot, so ASP.NET Core serves it as
# static files — this is what makes the app a single deployable container.
COPY --from=frontend-build /src/frontend/dist ./backend/Ao3Tracker.Api/wwwroot
RUN dotnet publish ./backend/Ao3Tracker.Api/Ao3Tracker.Api.csproj -c Release -o /app/publish --no-restore

# ---- Stage 3: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# /app/data holds the SQLite db file (default provider), the Data Protection key ring,
# and the admin-editable settings.json overlay — mount one volume there and the whole
# instance is durable across container recreation.
# The aspnet image already ships a non-root user `app` (uid/gid 1654) and carries no
# adduser/useradd to make another with. /app/data is chowned here rather than at runtime
# because a fresh named volume mounted over it inherits this directory's ownership.
RUN mkdir -p /app/data && chown -R app:app /app
COPY --from=backend-build --chown=app:app /app/publish .
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Storage__DataDirectory=/app/data
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ao3Tracker.Api.dll"]

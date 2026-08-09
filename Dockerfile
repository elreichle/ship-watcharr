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
RUN adduser --disabled-password --home /app --gecos '' appuser \
    && mkdir -p /app/keys \
    && chown -R appuser:appuser /app
COPY --from=backend-build --chown=appuser:appuser /app/publish .
USER appuser

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeyPath=/app/keys
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ao3Tracker.Api.dll"]

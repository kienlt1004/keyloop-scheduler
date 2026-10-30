# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first so the layer is cached until a csproj changes.
COPY Directory.Build.props ./
COPY src/Scheduler.Api/Scheduler.Api.csproj src/Scheduler.Api/
RUN dotnet restore src/Scheduler.Api/Scheduler.Api.csproj

COPY src/ src/
RUN dotnet publish src/Scheduler.Api/Scheduler.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run as the non-root user shipped with the .NET 8 images.
USER $APP_UID
ENTRYPOINT ["dotnet", "Scheduler.Api.dll"]

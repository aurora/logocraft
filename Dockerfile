# syntax=docker/dockerfile:1

# Build the Fable/Vite frontend and publish the ASP.NET server.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Vite 8 requires Node >= 20; the .NET SDK image does not include a suitable Node.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

COPY dotnet-tools.json ./
COPY src/Logo.Core/Logo.Core.fsproj src/Logo.Core/
COPY src/Logo.Client/Logo.Client.fsproj src/Logo.Client/package.json src/Logo.Client/package-lock.json src/Logo.Client/
COPY src/Logo.Server/Logo.Server.fsproj src/Logo.Server/

RUN dotnet tool restore \
    && dotnet restore src/Logo.Client/Logo.Client.fsproj \
    && dotnet restore src/Logo.Server/Logo.Server.fsproj

RUN cd src/Logo.Client && npm ci --no-audit --no-fund

COPY src/ ./src/
RUN cd src/Logo.Client && npm run build
RUN dotnet publish src/Logo.Server/Logo.Server.fsproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime image: only the published app and static frontend.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1
COPY --from=build /app/publish ./
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Logo.Server.dll"]

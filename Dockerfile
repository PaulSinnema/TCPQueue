# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["TCPQueue.csproj", "."]
RUN dotnet restore "./TCPQueue.csproj"

COPY . .
WORKDIR "/src/."
RUN dotnet build "./TCPQueue.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TCPQueue.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# FINAL STAGE – productie image
FROM base AS final
WORKDIR /app

# Kopieer ALLES vanuit publish
COPY --from=publish /app/publish .

# BELANGRIJK: verwijder alle appsettings*.json bestanden die per ongeluk meekomen
# Dit zorgt dat alleen de externe config via CONFIG_PATH wordt gebruikt
RUN rm -f appsettings.json appsettings.*.json

# Optioneel: maak de config map expliciet (voor duidelijkheid)
VOLUME /tcpqueue/config

# Zorg dat de container netjes stopt bij SIGTERM (Ctrl+C / docker stop)
STOPSIGNAL SIGTERM

ENTRYPOINT ["dotnet", "TCPQueue.dll"]
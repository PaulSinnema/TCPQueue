# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Create volume for external configuration
VOLUME /app/config

# Expose commonly used ports — extend as needed
EXPOSE 5000-5010/tcp

HEALTHCHECK CMD nc -z localhost 5000 || exit 1

ENTRYPOINT ["dotnet", "TcpQueueProxy.dll"]
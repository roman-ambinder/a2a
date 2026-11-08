# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.100 AS build
WORKDIR /src
COPY . .
RUN dotnet publish samples/MultiInstance/Agent/MultiInstanceAgent.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "MultiInstanceAgent.dll"]

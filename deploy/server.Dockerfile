# Servidor de sinalização do ZipStream. Build a partir da raiz do repositório:
#   docker build -f deploy/server.Dockerfile -t zipstream-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ZipP2P.Contracts/ZipP2P.Contracts.csproj src/ZipP2P.Contracts/
COPY src/ZipP2P.Server/ZipP2P.Server.csproj src/ZipP2P.Server/
RUN dotnet restore src/ZipP2P.Server/ZipP2P.Server.csproj
COPY src/ZipP2P.Contracts/ src/ZipP2P.Contracts/
COPY src/ZipP2P.Server/ src/ZipP2P.Server/
RUN dotnet publish src/ZipP2P.Server/ZipP2P.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 5000
ENTRYPOINT ["dotnet", "ZipP2P.Server.dll"]

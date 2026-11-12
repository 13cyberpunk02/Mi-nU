# MinU.Server. Built from the server folder: docker build -t minu-server server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so packages are restored again only when they change.
COPY MinU.Contracts/MinU.Contracts.csproj MinU.Contracts/
COPY MinU.Server/MinU.Server.csproj MinU.Server/
RUN dotnet restore MinU.Server/MinU.Server.csproj
COPY MinU.Contracts/ MinU.Contracts/
COPY MinU.Server/ MinU.Server/
RUN dotnet publish MinU.Server/MinU.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MinU.Server.dll"]

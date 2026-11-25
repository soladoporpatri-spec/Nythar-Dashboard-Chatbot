# Imagem do painel/API (usada pelo perfil "full" do docker-compose, item 91)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ClinicHair.WebApi.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:5150 \
    CLINICHAIR_STORAGE_ROOT=/data
VOLUME ["/data"]
EXPOSE 5150
USER $APP_UID
ENTRYPOINT ["dotnet", "ClinicHair.WebApi.dll"]

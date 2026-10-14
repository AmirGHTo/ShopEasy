FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Gateway/ShopEasy.Gateway/ShopEasy.Gateway.csproj", "src/Gateway/ShopEasy.Gateway/"]
RUN dotnet restore "src/Gateway/ShopEasy.Gateway/ShopEasy.Gateway.csproj"
COPY . .
WORKDIR "/src/src/Gateway/ShopEasy.Gateway"
RUN dotnet build "ShopEasy.Gateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ShopEasy.Gateway.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
RUN mkdir -p /data && chmod 777 /data
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Services/ShopEasy.InventoryService/ShopEasy.InventoryService.csproj", "src/Services/ShopEasy.InventoryService/"]
COPY ["src/Shared/ShopEasy.Shared/ShopEasy.Shared.csproj", "src/Shared/ShopEasy.Shared/"]
RUN dotnet restore "src/Services/ShopEasy.InventoryService/ShopEasy.InventoryService.csproj"
COPY . .
WORKDIR "/src/src/Services/ShopEasy.InventoryService"
RUN dotnet build "ShopEasy.InventoryService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.InventoryService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ShopEasy.InventoryService.dll"]

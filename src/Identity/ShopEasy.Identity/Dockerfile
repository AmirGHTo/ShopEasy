FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
RUN mkdir -p /data && chmod 777 /data
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libsqlite3-0 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Identity/ShopEasy.Identity/ShopEasy.Identity.csproj", "src/Identity/ShopEasy.Identity/"]
RUN dotnet restore "src/Identity/ShopEasy.Identity/ShopEasy.Identity.csproj"
COPY . .
WORKDIR "/src/src/Identity/ShopEasy.Identity"
RUN dotnet publish "ShopEasy.Identity.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ShopEasy.Identity.dll"]
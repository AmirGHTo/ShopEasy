FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
# apt-get upgrade brings the base image up to date with Debian's security fixes (the image is rebuilt
# only now and then). curl is only here for the container health checks; nothing else is installed.
RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/ShopEasy.OrderService/ShopEasy.OrderService.csproj", "src/Services/ShopEasy.OrderService/"]
COPY ["src/Shared/ShopEasy.Shared/ShopEasy.Shared.csproj", "src/Shared/ShopEasy.Shared/"]
RUN dotnet restore "src/Services/ShopEasy.OrderService/ShopEasy.OrderService.csproj"
COPY . .
WORKDIR "/src/src/Services/ShopEasy.OrderService"
RUN dotnet build "ShopEasy.OrderService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.OrderService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The service listens on 8080 (> 1024) and writes nothing to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.OrderService.dll"]

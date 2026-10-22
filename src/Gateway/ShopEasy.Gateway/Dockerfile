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
COPY ["src/Gateway/ShopEasy.Gateway/ShopEasy.Gateway.csproj", "src/Gateway/ShopEasy.Gateway/"]
RUN dotnet restore "src/Gateway/ShopEasy.Gateway/ShopEasy.Gateway.csproj"
COPY . .
WORKDIR "/src/src/Gateway/ShopEasy.Gateway"
RUN dotnet build "ShopEasy.Gateway.csproj" -c Release -o /app/build

# The storefront (React) is built here; only its built files reach the final image, Node does not.
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY ["src/Web/ShopEasy.Web/package.json", "src/Web/ShopEasy.Web/package-lock.json", "./"]
RUN npm ci
COPY src/Web/ShopEasy.Web/ ./
RUN npm run build

FROM build AS publish
RUN dotnet publish "ShopEasy.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# The built storefront, served by the gateway from wwwroot
COPY --from=web /web/dist ./wwwroot
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The service listens on 8080 (> 1024) and writes nothing to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.Gateway.dll"]

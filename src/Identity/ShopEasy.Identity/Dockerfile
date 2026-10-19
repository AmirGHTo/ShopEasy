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
COPY ["src/Identity/ShopEasy.Identity/ShopEasy.Identity.csproj", "src/Identity/ShopEasy.Identity/"]
RUN dotnet restore "src/Identity/ShopEasy.Identity/ShopEasy.Identity.csproj"
COPY . .
WORKDIR "/src/src/Identity/ShopEasy.Identity"
RUN dotnet publish "ShopEasy.Identity.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The service listens on 8080 (> 1024) and writes nothing to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.Identity.dll"]

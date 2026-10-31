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
COPY ["src/Services/ShopEasy.ProductService/ShopEasy.ProductService.csproj", "src/Services/ShopEasy.ProductService/"]
COPY ["src/Shared/ShopEasy.Shared/ShopEasy.Shared.csproj", "src/Shared/ShopEasy.Shared/"]
RUN dotnet restore "src/Services/ShopEasy.ProductService/ShopEasy.ProductService.csproj"
COPY . .
WORKDIR "/src/src/Services/ShopEasy.ProductService"
RUN dotnet build "ShopEasy.ProductService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.ProductService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# The commit the image is built from (CI passes it); every log line carries it as ServiceVersion.
# Last, so that a new commit does not invalidate the cached copy of the application above.
ARG SERVICE_VERSION=dev
ENV Service__Version=$SERVICE_VERSION
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The service listens on 8080 (> 1024) and writes nothing to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.ProductService.dll"]

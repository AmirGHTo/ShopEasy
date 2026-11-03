FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
# apt-get upgrade brings the base image up to date with Debian's security fixes (the image is rebuilt
# only now and then). Nothing else is installed: the probe has no health check, its metrics say how it is.
RUN apt-get update \
    && apt-get upgrade -y \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Tools/ShopEasy.Probe/ShopEasy.Probe.csproj", "src/Tools/ShopEasy.Probe/"]
RUN dotnet restore "src/Tools/ShopEasy.Probe/ShopEasy.Probe.csproj"
# the end-to-end tests' customer (Shopper), which the project compiles as source
COPY . .
WORKDIR "/src/src/Tools/ShopEasy.Probe"
RUN dotnet build "ShopEasy.Probe.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.Probe.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The only port is the metrics port (9091), and nothing is written to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.Probe.dll"]

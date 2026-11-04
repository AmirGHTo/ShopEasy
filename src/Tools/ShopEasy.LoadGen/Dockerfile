FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app
# apt-get upgrade brings the base image up to date with Debian's security fixes (the image is rebuilt only
# now and then). Nothing else is installed.
RUN apt-get update \
    && apt-get upgrade -y \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Tools/ShopEasy.LoadGen/ShopEasy.LoadGen.csproj", "src/Tools/ShopEasy.LoadGen/"]
RUN dotnet restore "src/Tools/ShopEasy.LoadGen/ShopEasy.LoadGen.csproj"
# the end-to-end tests' customer (Shopper), which the project compiles as source
COPY . .
WORKDIR "/src/src/Tools/ShopEasy.LoadGen"
RUN dotnet build "ShopEasy.LoadGen.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.LoadGen.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root. It listens on no port.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.LoadGen.dll"]

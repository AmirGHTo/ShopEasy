FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
# curl is only here for the container health checks; nothing else is installed
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/ShopEasy.PaymentService/ShopEasy.PaymentService.csproj", "src/Services/ShopEasy.PaymentService/"]
COPY ["src/Shared/ShopEasy.Shared/ShopEasy.Shared.csproj", "src/Shared/ShopEasy.Shared/"]
RUN dotnet restore "src/Services/ShopEasy.PaymentService/ShopEasy.PaymentService.csproj"
COPY . .
WORKDIR "/src/src/Services/ShopEasy.PaymentService"
RUN dotnet build "ShopEasy.PaymentService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ShopEasy.PaymentService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Run as the unprivileged "app" user the base image provides (UID 1654), not as root.
# The service listens on 8080 (> 1024) and writes nothing to the image's file system.
USER $APP_UID
ENTRYPOINT ["dotnet", "ShopEasy.PaymentService.dll"]

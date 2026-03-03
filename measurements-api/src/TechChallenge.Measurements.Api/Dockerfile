FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["measurements-api/src/TechChallenge.Measurements.Api/TechChallenge.Measurements.Api.csproj", "measurements-api/src/TechChallenge.Measurements.Api/"]
RUN dotnet restore "measurements-api/src/TechChallenge.Measurements.Api/TechChallenge.Measurements.Api.csproj"
COPY . .
WORKDIR "/src/measurements-api/src/TechChallenge.Measurements.Api"
RUN dotnet build "TechChallenge.Measurements.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "TechChallenge.Measurements.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TechChallenge.Measurements.Api.dll"]

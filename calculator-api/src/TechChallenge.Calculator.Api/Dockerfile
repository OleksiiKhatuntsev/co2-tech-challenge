FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["calculator-api/src/TechChallenge.Calculator.Api/TechChallenge.Calculator.Api.csproj", "calculator-api/src/TechChallenge.Calculator.Api/"]
RUN dotnet restore "calculator-api/src/TechChallenge.Calculator.Api/TechChallenge.Calculator.Api.csproj"
COPY . .
WORKDIR "/src/calculator-api/src/TechChallenge.Calculator.Api"
RUN dotnet build "TechChallenge.Calculator.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "TechChallenge.Calculator.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TechChallenge.Calculator.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["MaiImageGenerator.Jobs.BookPlanGeneration/MaiImageGenerator.Jobs.BookPlanGeneration.csproj", "MaiImageGenerator.Jobs.BookPlanGeneration/"]
COPY ["MaiImageGenerator.Shared/MaiImageGenerator.Shared.csproj", "MaiImageGenerator.Shared/"]
RUN dotnet restore "./MaiImageGenerator.Jobs.BookPlanGeneration/MaiImageGenerator.Jobs.BookPlanGeneration.csproj"
COPY . .
WORKDIR "/src/MaiImageGenerator.Jobs.BookPlanGeneration"
RUN dotnet build "./MaiImageGenerator.Jobs.BookPlanGeneration.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MaiImageGenerator.Jobs.BookPlanGeneration.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MaiImageGenerator.Jobs.BookPlanGeneration.dll"]

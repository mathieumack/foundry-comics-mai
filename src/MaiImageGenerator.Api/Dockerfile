# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
# 
# This solution is provided for testing, learning, and evaluation purposes only.
# It is not intended for production use. Microsoft provides no support or guarantees.
# Use at your own risk.
# This stage is used when running from VS in fast mode (Default for Debug configuration)
# Build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24 AS build
WORKDIR /src

# The build context is src so project references remain available.
COPY . .

RUN dotnet restore MaiImageGenerator.Api/MaiImageGenerator.Api.csproj
RUN dotnet publish MaiImageGenerator.Api/MaiImageGenerator.Api.csproj -c Release --no-restore -o /app

# Run the application
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24 AS final
WORKDIR /app

# Copy everything needed to run the app from the "build" stage.
COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "MaiImageGenerator.Api.dll"]
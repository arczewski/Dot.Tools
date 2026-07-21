# MSBuildWorkspace requires the .NET SDK at runtime for project-aware tools.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY DotMcp.Assembly.csproj ./
RUN dotnet restore DotMcp.Assembly.csproj

COPY . ./
RUN dotnet publish DotMcp.Assembly.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "DotMcp.Assembly.dll"]

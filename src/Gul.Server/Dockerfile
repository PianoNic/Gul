# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-deps
WORKDIR /build
COPY ["src/Gul.Server/Gul.Server.csproj", "src/Gul.Server/"]
COPY ["src/Directory.Build.props", "src/"]
RUN dotnet restore "src/Gul.Server/Gul.Server.csproj"

FROM backend-deps AS backend-build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.1.0
COPY src ./src
COPY application.properties ./application.properties
RUN sed -i "s|<version>.*</version>|<version>${VERSION}</version>|" application.properties
RUN dotnet publish "src/Gul.Server/Gul.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 ASPNETCORE_ENVIRONMENT=Production
COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "Gul.Server.dll"]

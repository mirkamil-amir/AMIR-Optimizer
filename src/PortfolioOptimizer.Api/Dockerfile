# Build context: repository root (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# global.json is intentionally not copied: the image's 10.0 SDK may be a different feature band.
COPY Directory.Build.props PortfolioOptimizer.sln ./
COPY src/ src/
RUN dotnet publish src/PortfolioOptimizer.Api/PortfolioOptimizer.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R app:app /app/data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/app/data/portfolio.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioOptimizer.Api.dll"]

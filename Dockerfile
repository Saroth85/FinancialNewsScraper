# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FinancialNewsScraper/FinancialNewsScraper.csproj FinancialNewsScraper/
RUN dotnet restore FinancialNewsScraper/FinancialNewsScraper.csproj
COPY . .
RUN dotnet publish FinancialNewsScraper/FinancialNewsScraper.csproj -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "FinancialNewsScraper.dll"]

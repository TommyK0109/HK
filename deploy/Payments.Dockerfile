FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Directory.Build.props src/
COPY src/Shared/HKShop.Contracts/ src/Shared/HKShop.Contracts/
COPY src/BuildingBlocks/HKShop.ServiceDefaults/ src/BuildingBlocks/HKShop.ServiceDefaults/
COPY src/BuildingBlocks/HKShop.Messaging/ src/BuildingBlocks/HKShop.Messaging/
COPY src/Services/Payments/HKShop.Payments.Api/ src/Services/Payments/HKShop.Payments.Api/
RUN dotnet publish src/Services/Payments/HKShop.Payments.Api -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "HKShop.Payments.Api.dll"]

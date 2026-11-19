FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Directory.Build.props src/
COPY src/Shared/HKShop.Contracts/ src/Shared/HKShop.Contracts/
COPY src/BuildingBlocks/HKShop.ServiceDefaults/ src/BuildingBlocks/HKShop.ServiceDefaults/
COPY src/BuildingBlocks/HKShop.Messaging/ src/BuildingBlocks/HKShop.Messaging/
COPY src/HKShop.Infrastructure/Email/SmtpSender.cs src/HKShop.Infrastructure/Email/
COPY src/Services/HKShop.Notification/ src/Services/HKShop.Notification/
RUN dotnet publish src/Services/HKShop.Notification -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER app
ENTRYPOINT ["dotnet", "HKShop.Notification.dll"]

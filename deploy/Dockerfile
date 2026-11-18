FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/ ./src/
RUN dotnet publish src/HKShop.Web/HKShop.Web.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /var/lib/hkshop/media /var/lib/hkshop/keys && chown -R app:app /var/lib/hkshop
COPY --from=build /out/ ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "HKShop.Web.dll"]

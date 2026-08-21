FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MealiePicnic/MealiePicnic.csproj src/MealiePicnic/
RUN dotnet restore src/MealiePicnic/MealiePicnic.csproj
COPY . .
RUN dotnet publish src/MealiePicnic/MealiePicnic.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "MealiePicnic.dll"]

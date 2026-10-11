# PioManage API. Build from the REPO ROOT so central package management applies:
#   docker build -f apps/api/Dockerfile -t desk-api .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY packages/ packages/
COPY apps/api/ apps/api/
RUN dotnet publish apps/api -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /out .
# Attachment bytes live on a mounted volume (Attachments__StoragePath points here).
RUN mkdir -p /data/attachments && chown -R app:app /data
USER app
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Desk.Api.dll"]

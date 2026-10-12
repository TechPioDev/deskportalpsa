# PioManage background worker (scheduled sync, job processing, reports).
# Build from the REPO ROOT: docker build -f apps/worker/Dockerfile -t desk-worker .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY packages/ packages/
COPY apps/worker/ apps/worker/
RUN dotnet publish apps/worker -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /out .
# Shares the attachment volume with the API: the sync engine writes imported files here too.
RUN mkdir -p /data/attachments && chown -R app:app /data
USER app
ENTRYPOINT ["dotnet", "Desk.Worker.dll"]

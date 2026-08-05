# DI-1 pattern B ("host-side"): dotnet-isolate runs on the host/CI runner before `docker build` -
# the test harness calls Pipeline.isolate in-process and points the build context at its output,
# so this Dockerfile never needs the .NET SDK to run `dotnet isolate` itself.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet publish ServiceA/ServiceA.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/ServiceA.dll"]

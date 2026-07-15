# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV GITHUB_ACTIONS=false
COPY nuget.config .
COPY .dependencies/ .dependencies/
COPY Legacy.Maliev.OrderService.Api/Legacy.Maliev.OrderService.Api.csproj Legacy.Maliev.OrderService.Api/
COPY Legacy.Maliev.OrderService.Application/Legacy.Maliev.OrderService.Application.csproj Legacy.Maliev.OrderService.Application/
COPY Legacy.Maliev.OrderService.Domain/Legacy.Maliev.OrderService.Domain.csproj Legacy.Maliev.OrderService.Domain/
COPY Legacy.Maliev.OrderService.Data/Legacy.Maliev.OrderService.Data.csproj Legacy.Maliev.OrderService.Data/
RUN dotnet restore Legacy.Maliev.OrderService.Api/Legacy.Maliev.OrderService.Api.csproj \
    -p:MalievWorkspaceRoot=/src/.dependencies
COPY . .
RUN dotnet publish Legacy.Maliev.OrderService.Api/Legacy.Maliev.OrderService.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false \
    -p:MalievWorkspaceRoot=/src/.dependencies

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Legacy.Maliev.OrderService.Api.dll"]

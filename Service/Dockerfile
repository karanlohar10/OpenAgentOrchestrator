# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Service/OpenAgentOrchestrator.Service.csproj", "Service/"]
COPY ["Command.Application/OpenAgentOrchestrator.Command.Application.csproj", "Command.Application/"]
COPY ["Command.Contract/OpenAgentOrchestrator.Command.Contract.csproj", "Command.Contract/"]
COPY ["Command.Domain.Model/OpenAgentOrchestrator.Command.Domain.Model.csproj", "Command.Domain.Model/"]
COPY ["Query.Application/OpenAgentOrchestrator.Query.Application.csproj", "Query.Application/"]
COPY ["Query.Domain.Model/OpenAgentOrchestrator.Query.Domain.Model.csproj", "Query.Domain.Model/"]

COPY Directory.Build.props .
RUN dotnet restore "./Service/OpenAgentOrchestrator.Service.csproj"

COPY . .
WORKDIR "/src/Service"
RUN dotnet build "./OpenAgentOrchestrator.Service.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./OpenAgentOrchestrator.Service.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# config.yaml holds real API keys/secrets for the hackathon demo - mount it as a volume /
# secret at runtime rather than baking it into the image (see .dockerignore).
ENTRYPOINT ["dotnet", "OpenAgentOrchestrator.Service.dll"]

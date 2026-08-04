FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Stripboard.Domain/Stripboard.Domain.csproj", "src/Stripboard.Domain/"]
COPY ["src/Stripboard.Application/Stripboard.Application.csproj", "src/Stripboard.Application/"]
COPY ["src/Stripboard.Infrastructure/Stripboard.Infrastructure.csproj", "src/Stripboard.Infrastructure/"]
COPY ["src/Stripboard.Solver/Stripboard.Solver.csproj", "src/Stripboard.Solver/"]
COPY ["src/Stripboard.CallSheets/Stripboard.CallSheets.csproj", "src/Stripboard.CallSheets/"]
COPY ["src/Stripboard.Mcp.Schedule/Stripboard.Mcp.Schedule.csproj", "src/Stripboard.Mcp.Schedule/"]
COPY ["src/Stripboard.Mcp.People/Stripboard.Mcp.People.csproj", "src/Stripboard.Mcp.People/"]
COPY ["src/Stripboard.Mcp.Locations/Stripboard.Mcp.Locations.csproj", "src/Stripboard.Mcp.Locations/"]
COPY ["src/Stripboard.Mcp.Weather/Stripboard.Mcp.Weather.csproj", "src/Stripboard.Mcp.Weather/"]
COPY ["src/Stripboard.Web/Stripboard.Web.csproj", "src/Stripboard.Web/"]
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]

RUN dotnet restore "src/Stripboard.Web/Stripboard.Web.csproj"

COPY . .
RUN dotnet publish "src/Stripboard.Web/Stripboard.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV PORT=8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Stripboard.Web.dll"]

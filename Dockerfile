FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ENV UseSystemVips=true
RUN apt-get update && apt-get install -y --no-install-recommends libvips42t64 libheif-plugin-libde265 libheif-plugin-aomdec libheif-plugin-aomenc && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY global.json Checkpoint.slnx ./
COPY src/Checkpoint.Api/Checkpoint.Api.csproj src/Checkpoint.Api/
COPY tests/Checkpoint.Tests/Checkpoint.Tests.csproj tests/Checkpoint.Tests/
RUN dotnet restore Checkpoint.slnx
COPY . .
RUN dotnet publish src/Checkpoint.Api -c Release --no-restore -o /app

FROM build AS test
ENTRYPOINT ["dotnet", "test", "Checkpoint.slnx", "--no-restore", "--logger", "console;verbosity=normal"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
RUN apt-get update && apt-get install -y --no-install-recommends libvips42t64 libheif-plugin-libde265 libheif-plugin-aomdec libheif-plugin-aomenc && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/checkpoint-data-protection \
    && chown $APP_UID:$APP_UID /var/lib/checkpoint-data-protection
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["bash","-c","exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Checkpoint.Api.dll"]

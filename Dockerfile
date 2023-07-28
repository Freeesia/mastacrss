FROM mcr.microsoft.com/dotnet/sdk:7.0-alpine AS build-env
WORKDIR /app
COPY . .
RUN dotnet publish -c Release -o out -r linux-x64

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0-alpine
WORKDIR /app
RUN apk add --no-cache supervisor libc6-compat

# supervisord.confをコピー
COPY docker/supervisord.conf /etc/supervisord.conf
COPY --from=build-env /app/out .
# supervisordを実行
ENTRYPOINT ["supervisord", "-c", "/etc/supervisord.conf"]

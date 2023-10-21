FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build-env
WORKDIR /app
COPY ./mastacrss .
ARG Version
ARG AssemblyVersion
ARG FileVersion
ARG InformationalVersion
RUN dotnet publish -c Release -o out -r linux-x64 \
    -p:Version=$Version \
    -p:AssemblyVersion=$AssemblyVersion \
    -p:FileVersion=$FileVersion \
    -p:InformationalVersion=$InformationalVersion

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0
WORKDIR /app
RUN apt-get update \
    && apt-get -y install supervisor \
    && rm -rf /var/lib/apt/lists/* \
    && sed -i 's/DEFAULT@SECLEVEL=2/DEFAULT@SECLEVEL=1/g' /etc/ssl/openssl.cnf

# supervisord.confをコピー
COPY docker/supervisord.conf /etc/supervisord.conf
COPY docker/entrypoint.sh /entrypoint.sh
COPY --from=build-env /app/out .
# supervisordを実行
ENTRYPOINT ["/entrypoint.sh"]

# Build the operator
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /operator

COPY ./ ./
RUN dotnet publish -c Release -o out -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION}

# The runner for the application
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final

# Install the helm CLI — the WorkloadReconciler shells out to `helm` to drive
# every workload deployment (helm upgrade --install / helm uninstall). Helm
# needs a writable config / cache directory, hence the HELM_*_HOME env vars
# below pointing at paths under /operator that the non-root operator-user owns.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \
    && curl -fsSL https://baltocdn.com/helm/signing.asc | gpg --dearmor -o /usr/share/keyrings/helm.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/helm.gpg] https://baltocdn.com/helm/stable/debian/ all main" > /etc/apt/sources.list.d/helm-stable-debian.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends helm \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -r k8s-operator && useradd -r -g k8s-operator -d /operator operator-user

WORKDIR /operator
COPY --from=build /operator/out/ ./
RUN mkdir -p /operator/.helm-config /operator/.helm-cache \
    && chown operator-user:k8s-operator -R /operator

USER operator-user

ENV HELM_CONFIG_HOME=/operator/.helm-config \
    HELM_CACHE_HOME=/operator/.helm-cache \
    HELM_DATA_HOME=/operator/.helm-config

ENTRYPOINT [ "dotnet", "Meshmakers.Octo.Communication.Operator.dll" ]

# Build the operator
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /operator

COPY ./ ./
RUN dotnet publish -c Release -o out

# The runner for the application
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim as final

RUN addgroup k8s-operator && useradd -G k8s-operator operator-user

WORKDIR /operator
COPY --from=build /operator/out/ ./
RUN chown operator-user:k8s-operator -R .

USER operator-user

ENTRYPOINT [ "dotnet", "Meshmakers.Octo.Communication.Operator.dll" ]

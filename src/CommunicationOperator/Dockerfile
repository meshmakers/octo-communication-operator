# Build the operator
FROM mcr.microsoft.com/dotnet/sdk:7.0-bullseye-slim AS build
WORKDIR /operator

COPY ./ ./
RUN curl -L -o cfssl https://github.com/cloudflare/cfssl/releases/download/v1.6.4/cfssl_1.6.4_linux_amd64
RUN curl -L -o cfssljson https://github.com/cloudflare/cfssl/releases/download/v1.6.4/cfssljson_1.6.4_linux_amd64
RUN chmod +x ./cfssl
RUN chmod +x ./cfssljson
RUN mkdir out
RUN cp cfssl cfssljson out/
RUN dotnet publish -c Release -o out src/CommunicationOperator/CommunicationOperator.csproj

# The runner for the application
FROM mcr.microsoft.com/dotnet/aspnet:7.0-bullseye-slim as final

RUN addgroup k8s-operator && useradd -G k8s-operator operator-user

WORKDIR /operator
COPY --from=build /operator/out/ ./
RUN chown operator-user:k8s-operator -R .

USER operator-user

ENTRYPOINT [ "dotnet", "Meshmakers.Octo.Communication.Operator.dll" ]

# OctoMesh Communication Operator


## Getting started as developer

Install kind (see for full documentation [here](https://kind.sigs.k8s.io/docs/user/quick-start/))

On macOS via Homebrew:
```bash
brew install kind
```

On Windows via Chocolatey (https://chocolatey.org/packages/kind)
```pwsh
choco install kind
```

On Windows via Winget (https://github.com/microsoft/winget-pkgs/tree/master/manifests/k/Kubernetes/kind)
```pwsh
winget install Kubernetes.kind
```

Create a cluster:
```bash
kind create cluster
```

Connect to the cluster:
```bash
kubectl cluster-info --context kind-kind
```


Install the operator:
```bash
make install
```


# Generate CRD and deployment files
```bash
dotnet kubeops g op meshmakers-octo-communication-operator ./CommunicationOperator.csproj --out config --clear-out
```


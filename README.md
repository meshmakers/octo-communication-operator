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
./src/scripts/Create-KindTestCluster.ps1
```

Connect to the cluster:
```bash
kubectl cluster-info --context kind-kind
```

Install the CRDs (located in the `octo-helm` repository):
```bash
cd ~/meshmakers/octo-helm/src 
helm install --namespace octo-operator-system --create-namespace octo-mesh-crds ./octo-mesh-crds/
```

In Program.cs set the IP address of your host system:
```csharp
    #if DEBUG || DEBUGL
    string ip = "192.168.15.66"; // Set the IP address of your host system
    ushort port = 6001;
    using CertificateGenerator generator = new CertificateGenerator(ip);
    using X509Certificate2 cert = generator.Server.CopyServerCertWithPrivateKey();
    #endif
```

Run the operator in debug mode

Apply secret and the pool to create a first communication:
```bash
kubectl create ns pool1
kubectl -n pool1 apply -f ./src/scripts/test-cluster-secret-local.yaml
kubectl -n pool1 apply -f ./src/scripts/test-cluster-pool-local.yaml
```


## During development


# Generate CRD and deployment files
```bash
dotnet kubeops g op meshmakers-octo-communication-operator ./CommunicationOperator.csproj --out config --clear-out
```


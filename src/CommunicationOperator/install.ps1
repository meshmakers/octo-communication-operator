kubectl --kubeconfig ~/k3s-kubeconfig apply -k config/install

kubectl --kubeconfig ~/k3s-kubeconfig -n meshmakers-octo-communication-operator-system get pods -w

dotnet kubeops g op meshmakers-octo-communication-operator ./CommunicationOperator.csproj --out config --clear-out
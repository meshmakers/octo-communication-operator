kubectl --kubeconfig ~/k3s-kubeconfig delete -k config/install
kubectl --kubeconfig ~/k3s-kubeconfig delete mutatingwebhookconfigurations mutators.plugoperator 
kubectl --kubeconfig ~/k3s-kubeconfig delete validatingwebhookconfigurations validators.plugoperator    
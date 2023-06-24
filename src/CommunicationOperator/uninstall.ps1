kubectl --kubeconfig ~/k3s-kubeconfig delete -k config/install
kubectl --kubeconfig ~/k3s-kubeconfig delete mutatingwebhookconfigurations mutators.meshmakers-octo-communication-operator
kubectl --kubeconfig ~/k3s-kubeconfig delete validatingwebhookconfigurations validators.meshmakers-octo-communication-operator
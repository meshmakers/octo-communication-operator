kubectl --kubeconfig ~/k3s-kubeconfig apply -k config/install

kubectl --kubeconfig ~/k3s-kubeconfig -n meshmakers-octo-communication-operator-system get pods -w


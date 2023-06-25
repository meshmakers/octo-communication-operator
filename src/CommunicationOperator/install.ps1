kubectl --kubeconfig ~/k3s-kubeconfig apply -k config/install

kubectl --kubeconfig ~/k3s-kubeconfig -n communication-operator-system get pods -w


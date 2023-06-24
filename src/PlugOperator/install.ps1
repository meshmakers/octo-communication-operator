kubectl --kubeconfig ~/k3s-kubeconfig apply -k config/install

kubectl --kubeconfig ~/k3s-kubeconfig -n plugoperator-system get pods -w


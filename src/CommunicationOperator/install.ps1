kubectl --kubeconfig ~/k3s-kubeconfig apply -k config/install

kubectl --kubeconfig ~/k3s-kubeconfig -n communicationoperator-system get pods -w


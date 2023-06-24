kubectl delete -k config/install
kubectl delete mutatingwebhookconfigurations mutators.plugoperator 
kubectl delete validatingwebhookconfigurations validators.plugoperator    
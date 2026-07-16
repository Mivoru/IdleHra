#!/usr/bin/env bash
#
# Local Kubernetes orchestration script for the FolkIdle server stack.
#
# Deploys Postgres and Redis (each with a PersistentVolumeClaim), generates
# the folkidle-server-secrets Secret from the credentials of those two
# in-cluster services plus a freshly generated JWT signing key, builds the
# server image and loads it into the detected local cluster, then applies
# server/deployment.yaml and server/hpa.yaml and waits for the resulting
# pods to pass their liveness and readiness probes.
#
# Supports both Minikube and k3s. Whichever is already running is preferred;
# otherwise Minikube is started if installed. Idempotent: everything this
# script creates lives in a single namespace (folkidle-local), which is
# deleted and recreated on every run, so re-running the script tears down
# and rebuilds the environment cleanly with no manual cleanup required.
#
# Must be run from the repository root.

set -euo pipefail

NAMESPACE="folkidle-local"
IMAGE_TAG="folkidle-server:latest"
# Context is server/ (not server/FolkIdle.Server/) so the build can see the
# sibling GameData/ directory - the Content Pipeline's JSON data lives there
# and Docker COPY cannot reach outside its build context. See
# FolkIdle.Server/Dockerfile's matching path layout.
SERVER_BUILD_CONTEXT="server"
SERVER_DOCKERFILE="server/FolkIdle.Server/Dockerfile"
DEPLOYMENT_MANIFEST="server/deployment.yaml"
HPA_MANIFEST="server/hpa.yaml"

POSTGRES_IMAGE="postgres:16-alpine"
POSTGRES_USER="postgres"
POSTGRES_PASSWORD="postgres"
POSTGRES_DB="folkidle_prod"
POSTGRES_SERVICE="folkidle-postgres"

PGBOUNCER_IMAGE="edoburu/pgbouncer:v1.24.1-p1"
PGBOUNCER_SERVICE="folkidle-pgbouncer"

REDIS_IMAGE="redis:7-alpine"
REDIS_SERVICE="folkidle-redis"

PROMETHEUS_IMAGE="prom/prometheus:v2.54.1"
PROMETHEUS_SERVICE="folkidle-prometheus"

PROMETHEUS_ADAPTER_IMAGE="registry.k8s.io/prometheus-adapter/prometheus-adapter:v0.11.2"
PROMETHEUS_ADAPTER_SERVICE="folkidle-prometheus-adapter"

CLUSTER_KIND=""

log() {
    echo "[deploy_local_k8s] $1"
}

fail() {
    echo "[deploy_local_k8s] ERROR: $1" >&2
    exit 1
}

require_repo_root() {
    if [ ! -f "$SERVER_DOCKERFILE" ] || [ ! -f "$DEPLOYMENT_MANIFEST" ] || [ ! -f "$HPA_MANIFEST" ]; then
        fail "expected to find $SERVER_DOCKERFILE, $DEPLOYMENT_MANIFEST and $HPA_MANIFEST relative to the current directory. Run this script from the repository root."
    fi
}

# Detects which local cluster tool to use. A cluster that is already running
# is preferred over starting a new one, so a developer who already has
# either tool up keeps using it. Falls back to starting Minikube if
# installed. Fails with a clear message naming both supported tools if
# neither is usable.
detect_cluster_tool() {
    if command -v minikube >/dev/null 2>&1 && minikube status >/dev/null 2>&1; then
        CLUSTER_KIND="minikube"
        log "detected a running Minikube cluster."
        return
    fi

    if command -v k3s >/dev/null 2>&1 && kubectl get nodes >/dev/null 2>&1; then
        CLUSTER_KIND="k3s"
        log "detected a running k3s cluster."
        return
    fi

    if command -v minikube >/dev/null 2>&1; then
        CLUSTER_KIND="minikube"
        log "no running cluster found; Minikube is installed, will start it."
        return
    fi

    if command -v k3s >/dev/null 2>&1; then
        CLUSTER_KIND="k3s"
        log "no running cluster found; k3s is installed, will start it."
        return
    fi

    fail "neither minikube nor k3s was found on PATH. Install one of them: Minikube - https://minikube.sigs.k8s.io/docs/start/ ; k3s - https://docs.k3s.io/quick-start"
}

ensure_cluster() {
    detect_cluster_tool

    if [ "$CLUSTER_KIND" = "minikube" ]; then
        if ! minikube status >/dev/null 2>&1; then
            log "starting Minikube..."
            minikube start
        fi
        kubectl config use-context minikube >/dev/null
    else
        if ! systemctl is-active --quiet k3s 2>/dev/null && ! pgrep -x k3s >/dev/null 2>&1; then
            log "starting k3s..."
            sudo systemctl start k3s
        fi
        export KUBECONFIG="${KUBECONFIG:-/etc/rancher/k3s/k3s.yaml}"
    fi

    command -v kubectl >/dev/null 2>&1 || fail "kubectl is required but was not found on PATH."
}

# Namespace-scoped teardown-and-rebuild: everything this script creates
# (Postgres, Redis, the Secret, and the applied Deployment/HPA) lives only
# inside folkidle-local, so deleting and recreating that one namespace is
# both sufficient and safe cleanup - it cannot touch any other workload in
# the cluster, and it guarantees a byte-for-byte fresh environment on every
# run instead of accumulating stale state across repeated invocations.
reset_namespace() {
    # The custom.metrics.k8s.io APIService registered by
    # deploy_prometheus_adapter is cluster-scoped, not namespaced, so it
    # outlives this namespace's own resources during teardown - the API
    # server then cannot complete namespace deletion at all, since it keeps
    # trying (and failing) to run discovery against that APIService's now-
    # deleted backing Service, permanently stalling with a
    # NamespaceDeletionDiscoveryFailure condition instead of a bounded
    # error. Deleting it first, before the namespace, avoids that
    # chicken-and-egg deadlock.
    log "removing the custom.metrics.k8s.io APIService from any previous run..."
    kubectl delete apiservice v1beta1.custom.metrics.k8s.io --ignore-not-found

    log "removing any previous folkidle-local namespace (idempotent teardown)..."
    kubectl delete namespace "$NAMESPACE" --ignore-not-found --wait=true --timeout=120s
    kubectl create namespace "$NAMESPACE"
}

deploy_postgres() {
    log "deploying Postgres ($POSTGRES_IMAGE) with a persistent volume..."
    kubectl -n "$NAMESPACE" apply -f - <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: ${POSTGRES_SERVICE}-pvc
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 1Gi
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${POSTGRES_SERVICE}
  labels:
    app: ${POSTGRES_SERVICE}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${POSTGRES_SERVICE}
  template:
    metadata:
      labels:
        app: ${POSTGRES_SERVICE}
    spec:
      containers:
      - name: postgres
        image: ${POSTGRES_IMAGE}
        ports:
        - containerPort: 5432
        env:
        - name: POSTGRES_USER
          value: "${POSTGRES_USER}"
        - name: POSTGRES_PASSWORD
          value: "${POSTGRES_PASSWORD}"
        - name: POSTGRES_DB
          value: "${POSTGRES_DB}"
        volumeMounts:
        - name: postgres-storage
          mountPath: /var/lib/postgresql/data
        readinessProbe:
          exec:
            command: ["pg_isready", "-U", "${POSTGRES_USER}"]
          initialDelaySeconds: 5
          periodSeconds: 5
      volumes:
      - name: postgres-storage
        persistentVolumeClaim:
          claimName: ${POSTGRES_SERVICE}-pvc
---
apiVersion: v1
kind: Service
metadata:
  name: ${POSTGRES_SERVICE}
spec:
  selector:
    app: ${POSTGRES_SERVICE}
  ports:
  - port: 5432
    targetPort: 5432
EOF

    kubectl -n "$NAMESPACE" rollout status deployment/"$POSTGRES_SERVICE" --timeout=180s
}

# A freshly deployed Postgres container has no schema at all - folkidle-server
# does not run migrations on startup (deliberately: applying migrations from
# every replica's own startup path would race multiple pods against the
# same migration history table), so without this step every folkidle-server
# pod crash-loops immediately on its first query with a bare
# "relation ... does not exist" error. Port-forwards Postgres out to the
# host temporarily, runs the same dotnet ef database update tooling used
# throughout this project's test fixtures, then tears the port-forward
# down - the cluster is never left with a port exposed beyond this step.
run_database_migrations() {
    log "applying EF Core migrations against $POSTGRES_SERVICE..."

    local local_port=15432
    local pf_log
    pf_log="$(mktemp)"
    kubectl -n "$NAMESPACE" port-forward deployment/"$POSTGRES_SERVICE" "${local_port}:5432" >"$pf_log" 2>&1 &
    local port_forward_pid=$!

    local waited=0
    while ! grep -q "Forwarding from" "$pf_log" 2>/dev/null; do
        if ! kill -0 "$port_forward_pid" 2>/dev/null; then
            cat "$pf_log" >&2
            rm -f "$pf_log"
            fail "kubectl port-forward to $POSTGRES_SERVICE exited before becoming ready."
        fi
        sleep 1
        waited=$((waited + 1))
        if [ "$waited" -ge 30 ]; then
            kill "$port_forward_pid" 2>/dev/null || true
            rm -f "$pf_log"
            fail "timed out waiting for the Postgres port-forward to become ready."
        fi
    done

    local migrate_status=0
    (
        cd server/FolkIdle.Server
        FOLKIDLE_DB_CONN="Host=127.0.0.1;Port=${local_port};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
            dotnet ef database update
    ) || migrate_status=$?

    kill "$port_forward_pid" 2>/dev/null || true
    wait "$port_forward_pid" 2>/dev/null || true
    rm -f "$pf_log"

    if [ "$migrate_status" -ne 0 ]; then
        fail "dotnet ef database update failed - see output above."
    fi
}

# Deploys pgbouncer as a connection pooler sitting between folkidle-server
# and Postgres. create_secrets points the server's connection string at this
# Service rather than at $POSTGRES_SERVICE directly, so every pooled
# connection request is served from a small, fixed pool of real Postgres
# backend connections instead of each server pod/request opening its own.
deploy_pgbouncer() {
    log "deploying pgbouncer ($PGBOUNCER_IMAGE) in front of $POSTGRES_SERVICE..."
    kubectl -n "$NAMESPACE" apply -f - <<EOF
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${PGBOUNCER_SERVICE}
  labels:
    app: ${PGBOUNCER_SERVICE}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${PGBOUNCER_SERVICE}
  template:
    metadata:
      labels:
        app: ${PGBOUNCER_SERVICE}
    spec:
      containers:
      - name: pgbouncer
        image: ${PGBOUNCER_IMAGE}
        ports:
        - containerPort: 5432
        env:
        - name: DB_HOST
          value: "${POSTGRES_SERVICE}"
        - name: DB_PORT
          value: "5432"
        - name: DB_USER
          value: "${POSTGRES_USER}"
        - name: DB_PASSWORD
          value: "${POSTGRES_PASSWORD}"
        - name: DB_NAME
          value: "${POSTGRES_DB}"
        - name: POOL_MODE
          value: "transaction"
        - name: MAX_CLIENT_CONN
          value: "1000"
        - name: DEFAULT_POOL_SIZE
          value: "50"
        - name: AUTH_TYPE
          value: "plain"
        readinessProbe:
          tcpSocket:
            port: 5432
          initialDelaySeconds: 5
          periodSeconds: 5
---
apiVersion: v1
kind: Service
metadata:
  name: ${PGBOUNCER_SERVICE}
spec:
  selector:
    app: ${PGBOUNCER_SERVICE}
  ports:
  - port: 6432
    targetPort: 5432
EOF

    kubectl -n "$NAMESPACE" rollout status deployment/"$PGBOUNCER_SERVICE" --timeout=180s
}

deploy_redis() {
    log "deploying Redis ($REDIS_IMAGE) with a persistent volume..."
    kubectl -n "$NAMESPACE" apply -f - <<EOF
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: ${REDIS_SERVICE}-pvc
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 1Gi
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${REDIS_SERVICE}
  labels:
    app: ${REDIS_SERVICE}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${REDIS_SERVICE}
  template:
    metadata:
      labels:
        app: ${REDIS_SERVICE}
    spec:
      containers:
      - name: redis
        image: ${REDIS_IMAGE}
        ports:
        - containerPort: 6379
        volumeMounts:
        - name: redis-storage
          mountPath: /data
        readinessProbe:
          exec:
            command: ["redis-cli", "ping"]
          initialDelaySeconds: 5
          periodSeconds: 5
      volumes:
      - name: redis-storage
        persistentVolumeClaim:
          claimName: ${REDIS_SERVICE}-pvc
---
apiVersion: v1
kind: Service
metadata:
  name: ${REDIS_SERVICE}
spec:
  selector:
    app: ${REDIS_SERVICE}
  ports:
  - port: 6379
    targetPort: 6379
EOF

    kubectl -n "$NAMESPACE" rollout status deployment/"$REDIS_SERVICE" --timeout=180s
}

# Modul: enables the metrics-server addon (Minikube-specific - k3s ships
# its own metrics-server by default and does not need this step), which is
# what the HPA's CPU/RAM Resource metric entry in hpa.yaml actually needs
# to resolve a value at all. Without it the HPA shows "unknown" for cpu
# utilization indefinitely and never scales on that metric.
enable_metrics_server() {
    if [ "$CLUSTER_KIND" = "minikube" ]; then
        log "enabling the metrics-server addon..."
        minikube addons enable metrics-server
    else
        log "k3s ships its own metrics-server by default - skipping addon enable."
    fi
}

# Deploys a minimal Prometheus scraping every pod in this namespace that
# carries the prometheus.io/scrape annotation (see deployment.yaml) - the
# same discovery convention real production Prometheus deployments use.
# This is what actually makes the custom Prometheus metrics in hpa.yaml
# (folkidle_active_sessions_total, folkidle_tick_duration_milliseconds)
# observable at all; without a Prometheus instance scraping the pods there
# is nothing for prometheus-adapter below to read.
deploy_prometheus() {
    log "deploying Prometheus ($PROMETHEUS_IMAGE) with pod-annotation scrape discovery..."
    kubectl -n "$NAMESPACE" apply -f - <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: ${PROMETHEUS_SERVICE}
---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: ${PROMETHEUS_SERVICE}
  namespace: ${NAMESPACE}
rules:
- apiGroups: [""]
  resources: ["pods"]
  verbs: ["get", "list", "watch"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: ${PROMETHEUS_SERVICE}
  namespace: ${NAMESPACE}
subjects:
- kind: ServiceAccount
  name: ${PROMETHEUS_SERVICE}
  namespace: ${NAMESPACE}
roleRef:
  kind: Role
  name: ${PROMETHEUS_SERVICE}
  apiGroup: rbac.authorization.k8s.io
---
apiVersion: v1
kind: ConfigMap
metadata:
  name: ${PROMETHEUS_SERVICE}-config
data:
  prometheus.yml: |
    global:
      scrape_interval: 10s
    scrape_configs:
      - job_name: folkidle-server
        kubernetes_sd_configs:
          - role: pod
            namespaces:
              names:
                - ${NAMESPACE}
        relabel_configs:
          - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_scrape]
            action: keep
            regex: "true"
          - source_labels: [__meta_kubernetes_pod_annotation_prometheus_io_path]
            action: replace
            target_label: __metrics_path__
            regex: (.+)
          - source_labels: [__address__, __meta_kubernetes_pod_annotation_prometheus_io_port]
            action: replace
            regex: ([^:]+)(?::\d+)?;(\d+)
            replacement: \$1:\$2
            target_label: __address__
          - source_labels: [__meta_kubernetes_pod_name]
            target_label: pod
          - source_labels: [__meta_kubernetes_namespace]
            target_label: namespace
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${PROMETHEUS_SERVICE}
  labels:
    app: ${PROMETHEUS_SERVICE}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${PROMETHEUS_SERVICE}
  template:
    metadata:
      labels:
        app: ${PROMETHEUS_SERVICE}
    spec:
      serviceAccountName: ${PROMETHEUS_SERVICE}
      containers:
      - name: prometheus
        image: ${PROMETHEUS_IMAGE}
        args:
          - --config.file=/etc/prometheus/prometheus.yml
        ports:
        - containerPort: 9090
        volumeMounts:
          - name: config
            mountPath: /etc/prometheus
        readinessProbe:
          httpGet:
            path: /-/ready
            port: 9090
          initialDelaySeconds: 5
          periodSeconds: 5
      volumes:
        - name: config
          configMap:
            name: ${PROMETHEUS_SERVICE}-config
---
apiVersion: v1
kind: Service
metadata:
  name: ${PROMETHEUS_SERVICE}
spec:
  selector:
    app: ${PROMETHEUS_SERVICE}
  ports:
  - port: 9090
    targetPort: 9090
EOF

    kubectl -n "$NAMESPACE" rollout status deployment/"$PROMETHEUS_SERVICE" --timeout=180s
}

# Deploys prometheus-adapter, which is what actually exposes Prometheus
# series as the custom.metrics.k8s.io API the HPA controller reads
# folkidle_active_sessions_total and folkidle_tick_duration_milliseconds
# from (see hpa.yaml's two Pods-type metric entries). Registers as a
# Kubernetes aggregated APIService - insecureSkipTLSVerify is used instead
# of a real CA bundle because this is a local development cluster with a
# self-signed serving certificate the adapter generates for itself at
# startup; a real deployment must supply and verify a proper certificate.
#
# folkidle_tick_duration_milliseconds is a Prometheus histogram (_bucket/
# _sum/_count series, not a single value - see NetworkBroadcastSystem.
# HandleMetrics) - a bare HPA reference to that name cannot resolve to
# anything on its own. The adapter rule below is what actually makes it
# resolve: it computes the mean tick duration
# (folkidle_tick_duration_milliseconds_sum / ..._count) per pod and
# exposes that under the exact name folkidle_tick_duration_milliseconds,
# which is the piece hpa.yaml alone cannot provide.
deploy_prometheus_adapter() {
    log "deploying prometheus-adapter ($PROMETHEUS_ADAPTER_IMAGE)..."
    kubectl -n "$NAMESPACE" apply -f - <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}
rules:
- apiGroups: [""]
  resources: ["pods", "nodes", "namespaces", "services"]
  verbs: ["get", "list", "watch"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: ${PROMETHEUS_ADAPTER_SERVICE}
subjects:
- kind: ServiceAccount
  name: ${PROMETHEUS_ADAPTER_SERVICE}
  namespace: ${NAMESPACE}
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}-auth-delegator
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: system:auth-delegator
subjects:
- kind: ServiceAccount
  name: ${PROMETHEUS_ADAPTER_SERVICE}
  namespace: ${NAMESPACE}
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: custom-metrics-server-resources
rules:
- apiGroups: ["custom.metrics.k8s.io"]
  resources: ["*"]
  verbs: ["*"]
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: hpa-controller-custom-metrics
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: custom-metrics-server-resources
subjects:
# Covers both possible identities the HPA controller may run as, depending
# on whether the cluster's kube-controller-manager was started with
# --use-service-account-credentials (kubeadm-based clusters, including
# Minikube, typically enable this, giving each controller loop its own
# ServiceAccount in kube-system) or not (falling back to the well-known
# system:kube-controller-manager user).
- kind: ServiceAccount
  name: horizontal-pod-autoscaler
  namespace: kube-system
- kind: User
  name: system:kube-controller-manager
  apiGroup: rbac.authorization.k8s.io
---
apiVersion: v1
kind: ConfigMap
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}-config
data:
  config.yaml: |
    rules:
    - seriesQuery: 'folkidle_active_sessions_total{namespace!="",pod!=""}'
      resources:
        overrides:
          namespace: {resource: "namespace"}
          pod: {resource: "pod"}
      name:
        matches: "folkidle_active_sessions_total"
        as: "folkidle_active_sessions_total"
      metricsQuery: 'avg(<<.Series>>{<<.LabelMatchers>>}) by (<<.GroupBy>>)'
    - seriesQuery: 'folkidle_tick_duration_milliseconds_sum{namespace!="",pod!=""}'
      resources:
        overrides:
          namespace: {resource: "namespace"}
          pod: {resource: "pod"}
      name:
        matches: "folkidle_tick_duration_milliseconds_sum"
        as: "folkidle_tick_duration_milliseconds"
      # Modul: mean tick duration = sum/count over a rolling 5-minute window
      # (rate() over a matching window on both series, then divided), not a
      # single instantaneous sample - this is the histogram-to-scalar
      # derivation the HPA needs, since folkidle_tick_duration_milliseconds
      # itself is only ever emitted as _sum/_count/_bucket series (see
      # NetworkBroadcastSystem.HandleMetrics), never as a bare gauge.
      metricsQuery: 'avg(rate(folkidle_tick_duration_milliseconds_sum{<<.LabelMatchers>>}[5m]) / rate(folkidle_tick_duration_milliseconds_count{<<.LabelMatchers>>}[5m])) by (<<.GroupBy>>)'
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}
  labels:
    app: ${PROMETHEUS_ADAPTER_SERVICE}
spec:
  replicas: 1
  selector:
    matchLabels:
      app: ${PROMETHEUS_ADAPTER_SERVICE}
  template:
    metadata:
      labels:
        app: ${PROMETHEUS_ADAPTER_SERVICE}
    spec:
      serviceAccountName: ${PROMETHEUS_ADAPTER_SERVICE}
      containers:
      - name: prometheus-adapter
        image: ${PROMETHEUS_ADAPTER_IMAGE}
        args:
          - --prometheus-url=http://${PROMETHEUS_SERVICE}.${NAMESPACE}.svc.cluster.local:9090
          - --metrics-relist-interval=30s
          - --config=/etc/adapter/config.yaml
          - --secure-port=6443
          # Default working directory is not writable in this image - the
          # adapter's self-signed serving certificate generation fails with
          # a permission error unless redirected to a writable path.
          - --cert-dir=/tmp/prometheus-adapter-certs
        ports:
        - containerPort: 6443
        volumeMounts:
          - name: config
            mountPath: /etc/adapter
      volumes:
        - name: config
          configMap:
            name: ${PROMETHEUS_ADAPTER_SERVICE}-config
---
apiVersion: v1
kind: Service
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}
spec:
  selector:
    app: ${PROMETHEUS_ADAPTER_SERVICE}
  ports:
  - port: 443
    targetPort: 6443
---
apiVersion: apiregistration.k8s.io/v1
kind: APIService
metadata:
  name: v1beta1.custom.metrics.k8s.io
spec:
  service:
    name: ${PROMETHEUS_ADAPTER_SERVICE}
    namespace: ${NAMESPACE}
  group: custom.metrics.k8s.io
  version: v1beta1
  insecureSkipTLSVerify: true
  groupPriorityMinimum: 100
  versionPriority: 100
EOF

    # Applied separately, without -n "$NAMESPACE" - this RoleBinding
    # declares its own namespace: kube-system (the aggregation layer's
    # authentication-reader Role lives only there), which conflicts with a
    # -n flag naming a different namespace on the same kubectl apply call.
    kubectl apply -f - <<EOF
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: ${PROMETHEUS_ADAPTER_SERVICE}-auth-reader
  namespace: kube-system
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: Role
  name: extension-apiserver-authentication-reader
subjects:
- kind: ServiceAccount
  name: ${PROMETHEUS_ADAPTER_SERVICE}
  namespace: ${NAMESPACE}
EOF

    kubectl -n "$NAMESPACE" rollout status deployment/"$PROMETHEUS_ADAPTER_SERVICE" --timeout=180s
}

# Builds folkidle-server-secrets from the Postgres/Redis services just
# deployed (in-cluster DNS names, so this only ever works from inside the
# cluster - matching how deployment.yaml already expects to consume it) plus
# a JWT signing key generated fresh on every run. Never hardcodes a real
# secret value in this script - see Program.cs's isProductionForJwt guard,
# which requires JWT_SECRET_KEY to be set whenever DOTNET_ENVIRONMENT is
# Production, exactly the mode deployment.yaml runs in.
create_secrets() {
    log "generating folkidle-server-secrets (fresh JWT key each run)..."

    local jwt_secret
    jwt_secret="$(openssl rand -hex 32)"

    # Routed through pgbouncer, not $POSTGRES_SERVICE directly - see
    # deploy_pgbouncer.
    local postgres_conn="Host=${PGBOUNCER_SERVICE}.${NAMESPACE}.svc.cluster.local;Port=6432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
    local redis_conn="${REDIS_SERVICE}.${NAMESPACE}.svc.cluster.local:6379"

    kubectl -n "$NAMESPACE" create secret generic folkidle-server-secrets \
        --from-literal=postgres-connection-string="$postgres_conn" \
        --from-literal=redis-connection-string="$redis_conn" \
        --from-literal=jwt-secret-key="$jwt_secret"
}

# Builds the server image locally and loads it directly into the detected
# cluster's own image store, bypassing any external registry - the standard
# approach for both supported tools (minikube image load / a k3s containerd
# import), since deployment.yaml pins imagePullPolicy: IfNotPresent against
# a bare "folkidle-server:latest" tag with no registry prefix.
build_and_load_image() {
    log "building $IMAGE_TAG from $SERVER_DOCKERFILE (context: $SERVER_BUILD_CONTEXT)..."
    docker build -f "$SERVER_DOCKERFILE" -t "$IMAGE_TAG" "$SERVER_BUILD_CONTEXT"

    if [ "$CLUSTER_KIND" = "minikube" ]; then
        log "loading $IMAGE_TAG into Minikube..."
        minikube image load "$IMAGE_TAG"
    else
        log "importing $IMAGE_TAG into k3s containerd..."
        docker save "$IMAGE_TAG" | sudo k3s ctr images import -
    fi
}

apply_manifests() {
    log "applying $DEPLOYMENT_MANIFEST and $HPA_MANIFEST..."
    kubectl -n "$NAMESPACE" apply -f "$DEPLOYMENT_MANIFEST" -f "$HPA_MANIFEST"
}

# kubectl rollout status blocks until every pod in the Deployment is Ready,
# and a pod is only marked Ready once its readinessProbe passes (with
# livenessProbe failures triggering pod restarts along the way) - this is
# the standard, race-free way to "wait for pods to pass liveness and
# readiness probes" rather than a custom polling loop against /health/*
# directly, which would have to re-implement the same logic kubectl already
# provides.
wait_for_probes() {
    log "waiting for folkidle-server pods to become ready (liveness/readiness probes)..."
    kubectl -n "$NAMESPACE" rollout status deployment/folkidle-server --timeout=180s
}

print_summary() {
    log "deployment complete."
    echo
    echo "Namespace:        $NAMESPACE"
    echo "Server image:     $IMAGE_TAG"
    echo "Postgres service: ${POSTGRES_SERVICE}.${NAMESPACE}.svc.cluster.local:5432"
    echo "pgbouncer service: ${PGBOUNCER_SERVICE}.${NAMESPACE}.svc.cluster.local:6432"
    echo "Redis service:    ${REDIS_SERVICE}.${NAMESPACE}.svc.cluster.local:6379"
    echo "Prometheus:       kubectl -n $NAMESPACE port-forward deployment/${PROMETHEUS_SERVICE} 9090:9090"
    echo "Custom metrics:   kubectl get --raw \"/apis/custom.metrics.k8s.io/v1beta1/namespaces/$NAMESPACE/pods/*/folkidle_active_sessions_total\""
    echo
    echo "Reach the server locally with:"
    echo "  kubectl -n $NAMESPACE port-forward deployment/folkidle-server 8080:8080"
    echo "  (deployment.yaml defines no Service for folkidle-server itself, only the Deployment)"
    echo
    echo "Tear down manually with:"
    echo "  kubectl delete namespace $NAMESPACE"
}

main() {
    require_repo_root
    ensure_cluster
    enable_metrics_server
    reset_namespace
    deploy_postgres
    run_database_migrations
    deploy_pgbouncer
    deploy_redis
    deploy_prometheus
    deploy_prometheus_adapter
    create_secrets
    build_and_load_image
    apply_manifests
    wait_for_probes
    print_summary
}

main "$@"

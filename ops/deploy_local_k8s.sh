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

PGBOUNCER_IMAGE="edoburu/pgbouncer:1.21.0"
PGBOUNCER_SERVICE="folkidle-pgbouncer"

REDIS_IMAGE="redis:7-alpine"
REDIS_SERVICE="folkidle-redis"

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
    reset_namespace
    deploy_postgres
    deploy_pgbouncer
    deploy_redis
    create_secrets
    build_and_load_image
    apply_manifests
    wait_for_probes
    print_summary
}

main "$@"

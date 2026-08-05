# The web client, built and baked into the Caddy image that serves it.
#
# Build context is the REPOSITORY ROOT, not client_web. The artwork lives in
# client/Assets/Images/SpritesWeb - deliberately outside the client package so
# the Unity tree and the web build cannot drift - and vite.config.ts's
# copySprites plugin reaches up to it at build time. A context of client_web
# would silently produce a bundle with no artwork: the plugin warns and
# continues rather than failing, so every icon would fall back to initials and
# the build would still look successful.

FROM node:22-alpine AS build
WORKDIR /app

# Dependencies first, so a source-only change does not re-run npm ci.
COPY client_web/package.json client_web/package-lock.json ./
RUN npm ci

COPY client_web/ ./
# Reached by ../client/... from /app, matching the layout in a checkout.
COPY client/Assets/Images/SpritesWeb /client/Assets/Images/SpritesWeb

# Where the client will find its server. Passed at build time because Vite
# inlines it - there is no runtime configuration in a static bundle.
ARG VITE_FOLKIDLE_SERVER
ENV VITE_FOLKIDLE_SERVER=${VITE_FOLKIDLE_SERVER}

# `npx vite build`, NOT `npm run build`. That script chains generate:protocol,
# which shells out to `dotnet` with no fallback, and this image has Node only.
# Both generated files are committed; regeneration belongs locally and in CI.
RUN npx vite build

# Fail loudly if the artwork did not make it. The plugin only warns, and a
# silent art-free deploy is exactly the kind of thing nobody notices until a
# player asks why every item is two letters in a box.
RUN test -d dist/sprites && [ "$(find dist/sprites -type f | wc -l)" -gt 100 ] \
    || (echo "BUILD FAILED: dist/sprites is missing or nearly empty" && exit 1)

FROM caddy:2-alpine
COPY --from=build /app/dist /srv

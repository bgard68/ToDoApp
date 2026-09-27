# Build the React app and serve the static bundle with nginx.
# Build context is this directory: docker build -t todoapp-web .

# Base images pinned by DIGEST, not tag (review finding M10). A tag is mutable: the same
# Dockerfile can produce a different image tomorrow, so a reproducible build and an audited base
# are impossible with tags alone. Refresh with:
#   docker buildx imagetools inspect node:22-alpine --format '{{.Manifest.Digest}}'
FROM node:26-alpine@sha256:0b36e8c136b94cd4fcf02188228e76c31ad5872eef3fec8cbd2eee500cfd9e80 AS build
WORKDIR /app

# npm ci, not npm install: ci installs exactly what package-lock.json records and fails if the
# lock file and package.json disagree. `npm install` would happily resolve something newer, so the
# image could ship a dependency tree nobody reviewed — and a different one from CI, which already
# used npm ci (review finding M10).
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
# VITE_API_URL is left empty so the SPA calls /api on its own origin;
# nginx (below) proxies /api to the API container.
RUN npm run build

# Unprivileged nginx: runs as uid 101 and listens on 8080, because a non-root process cannot bind
# a port below 1024. The stock nginx image runs its master process as root (review finding M10).
FROM nginxinc/nginx-unprivileged:alpine@sha256:6a23acdfca2b9cfbcec61419e3f1426bcbedb91362f2f19306a8567423bb4612 AS final
# The digest pin above buys reproducibility, but it also freezes the OS package set at whatever
# nginxinc last baked in. Alpine ships its security fixes on a different schedule, so the image
# scan gate blocks on fixable CRITICAL/HIGH findings long before the publisher rebuilds — and
# refreshing the pin, which is what the gate's own error message advises, does not help. Patch the
# whole package set forward here instead. Upgrading every package rather than a named one is
# deliberate: pinning one package only moves the failure to the next package Alpine patches first,
# which is the loop this replaces. The digest still fixes the starting point and npm ci still locks
# the app tree; what floats is the OS security-patch stream within Alpine 3.24, which is the part
# that should float.
USER root
RUN apk --no-cache upgrade
USER 101
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/ || exit 1

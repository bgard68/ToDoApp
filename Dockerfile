# Build the React app and serve the static bundle with nginx.
# Build context is this directory: docker build -t todoapp-web .

FROM node:22-alpine AS build
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
FROM nginxinc/nginx-unprivileged:alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/ || exit 1

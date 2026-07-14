# Build the React app and serve the static bundle with nginx.
# Build context is the frontend/ directory: docker build -t todoapp-web ./frontend

FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm install
COPY . .
# VITE_API_URL is left empty so the SPA calls /api on its own origin;
# nginx (below) proxies /api to the API container.
RUN npm run build

FROM nginx:alpine AS final
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80

# Desk Portal web (Next.js, standalone output).
# Build from the REPO ROOT: docker build -f apps/web/Dockerfile -t desk-web .
FROM node:22-alpine AS build
WORKDIR /src
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci
COPY apps/web/ .
# Auth/Keycloak URLs are read at RUNTIME (server-side), so no build-time secrets are baked in.
# The CSP header is fixed at BUILD time (next.config headers are compiled into the route
# manifest), so enforcing it is a build argument: --build-arg CSP_ENFORCE=true.
ARG CSP_ENFORCE=false
ENV CSP_ENFORCE=$CSP_ENFORCE
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /src/.next/standalone ./
COPY --from=build /src/.next/static ./.next/static
COPY --from=build /src/public ./public
USER node
ENV PORT=3000 HOSTNAME=0.0.0.0
EXPOSE 3000
CMD ["node", "server.js"]

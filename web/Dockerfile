# Reference production image for the Caisson SPA (security-review-5 #19). Multi-stage: build the
# Angular production bundle, then serve it via nginx with the strict CSP in nginx.conf. Serves the
# PRODUCTION configuration only (never build:e2e / build:harness — those carry the auth bypass).
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# Which Angular build configuration to bundle. Defaults to the real production build; the TEST
# deployment passes NG_CONFIG=azuredemo (auth-bypass demo pointed at the deployed Caisson.Api).
ARG NG_CONFIG=production
RUN npx ng build --configuration ${NG_CONFIG}

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/web/browser /usr/share/nginx/html
EXPOSE 8080

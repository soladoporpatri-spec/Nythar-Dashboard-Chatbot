# Imagem do WhatsApp Bridge (perfil "full" do docker-compose, item 91)
FROM node:22-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY tsconfig.json ./
COPY src ./src
RUN npm run build && npm prune --omit=dev

FROM node:22-slim
WORKDIR /app
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/dist ./dist
COPY package.json ./
ENV NODE_ENV=production
VOLUME ["/app/storage"]
EXPOSE 3100
USER node
CMD ["node", "dist/index.js"]

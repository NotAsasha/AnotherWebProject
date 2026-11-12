import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  server: {
    proxy: {
      "/api": "http://localhost:5297",
      "/hubs": { target: "http://localhost:5297", ws: true },
    },
  },
});

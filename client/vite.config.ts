import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/api': 'http://localhost:5297',
      '/hubs': { target: 'http://localhost:5297', ws: true },
    },
  },
})

import { defineConfig } from "@playwright/test";

const external = process.env.E2E_BASE_URL;

export default defineConfig({
  testDir: "./tests/e2e",
  outputDir: "./node_modules/.cache/e2e-results",
  workers: 1,
  timeout: 60000,
  use: {
    baseURL: external ?? "http://127.0.0.1:4174",
    channel: "chrome",
    viewport: { width: 1440, height: 1050 },
  },
  webServer: external
    ? undefined
    : {
        command: "npm run dev -- --host 127.0.0.1 --port 4174 --strictPort",
        url: "http://127.0.0.1:4174",
        reuseExistingServer: false,
        timeout: 30000,
      },
});

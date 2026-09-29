import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  use: {
    baseURL: 'http://127.0.0.1:4173',
    channel: 'chrome',
    viewport: { width: 1280, height: 900 },
  },
  webServer: [
    {
      command: 'dotnet run --project server/SignalStore.Api.csproj --no-launch-profile',
      url: 'http://127.0.0.1:5080/api/products',
      timeout: 120000,
      reuseExistingServer: false,
    },
    {
      command: 'npm run dev -- --host 127.0.0.1 --port 4173',
      url: 'http://127.0.0.1:4173',
      timeout: 120000,
      reuseExistingServer: false,
    },
  ],
});

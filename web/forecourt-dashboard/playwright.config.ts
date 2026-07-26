import { defineConfig } from '@playwright/test';
import { resolve } from 'node:path';

// The Playwright config is loaded as CommonJS, so __dirname is the portable way to anchor
// paths here; the application itself is ESM throughout.
const repoRoot = resolve(__dirname, '../..');

/**
 * The end-to-end run drives the whole stack, not a mock of it: a real site controller with the
 * real pump firmware behind it, and the dashboard talking to it through the dev-server proxy.
 *
 * The controller runs with an in-memory journal and the in-process acquirer, so a run leaves
 * nothing behind and needs no Docker. What it does need is the firmware host build; `npm run
 * e2e` builds it first, and `PUMP_FIRMWARE` points here.
 */
const firmware = process.env['PUMP_FIRMWARE'] ?? resolve(repoRoot, 'firmware/pump/build-host/pump_host');

export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  reporter: process.env['CI'] ? 'line' : 'list',
  use: { baseURL: 'http://localhost:4200', trace: 'retain-on-failure' },
  webServer: [
    {
      // The real acquirer simulator, so the trace carries real ISO 8583 messages rather than
      // the in-process stand-in's decisions.
      command: `dotnet run --project ${resolve(repoRoot, 'src/OpenForecourt.HostSimulator')} -c Release -- serve --port 9583`,
      port: 9583,
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
    {
      command: `dotnet run --project ${resolve(repoRoot, 'src/OpenForecourt.SiteController')} -c Release --urls http://localhost:8080`,
      url: 'http://localhost:8080/health',
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
      stdout: 'pipe',
      env: {
        Site__JournalPath: ':memory:',
        Site__HostEndpoint: 'localhost:9583',
        Site__PumpFirmwarePath: firmware,
        Site__PumpCount: '8',
        Site__PumpTickMs: '50',
        Site__PumpFlowMlPerTick: '400',
        Site__CardProfilesPath: resolve(repoRoot, 'tests/testdata/cards'),
        Site__PinBdkPath: resolve(repoRoot, 'tests/testdata/keys/bdk.hex'),
      },
    },
    {
      command: 'npm run start -- --port 4200',
      url: 'http://localhost:4200',
      reuseExistingServer: !process.env['CI'],
      timeout: 180_000,
    },
  ],
});

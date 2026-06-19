<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/playwright-setup/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed APS workspace names and ADO pipeline YAML; CI/CD section updated for GitHub Actions + dotnet; APS developer guide link removed)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: playwright-setup
description: >-
  Set up Playwright E2E test framework with project-specific configuration
---

# Playwright Setup Skill

Help developers set up Playwright testing for this project.

## When to Use

Use this skill when developers need help with:
- Setting up Playwright in a new or existing project
- Configuring Playwright for local and CI runs
- Creating test files and page objects
- Troubleshooting Playwright issues

## PinballWizard Playwright Notes

This project already has Playwright E2E tests. Key facts:
- Tests live under `tests/PinballWizard.Web.E2ETests/`
- CI runs in the `ui-tests` GitHub Actions job (non-required)
- Headed Edge required for the pinwiz.ai Cloudflare OTP gate (headless is WAF-blocked)
- `storageState` skips the OTP gate after first authentication
- `MudTextField` needs `pressSequentially` (not `fill`) for MudBlazor inputs
- See memory `reference_pinwiz_smoke_automation` for full automation context

## Setup Steps

### 1. Install Dependencies

```bash
# Install Playwright
npm install -D @playwright/test

# Install browsers
npx playwright install
```

### 2. Create Base Config (playwright.config.ts)

```typescript
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? undefined : undefined,
  reporter: [
    ['html'],
    ['list']
  ],
  use: {
    baseURL: process.env.TEST_BASE_URL || 'http://localhost:3000',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'firefox',
      use: { ...devices['Desktop Firefox'] },
    },
    {
      name: 'webkit',
      use: { ...devices['Desktop Safari'] },
    },
  ],
});
```

### 3. Create .env.example

```bash
# Test target URL
TEST_BASE_URL=https://pinwiz.ai
```

### 4. Update .gitignore

```gitignore
# Playwright
/test-results/
/playwright-report/
/blob-report/
/playwright/.cache/
.env
```

### 5. Add npm Scripts (package.json)

```json
{
  "scripts": {
    "test": "playwright test",
    "test:headed": "playwright test --headed",
    "test:ui": "playwright test --ui",
    "test:report": "playwright show-report"
  }
}
```

## Test File Patterns

### Basic Test Structure

```typescript
import { test, expect } from '@playwright/test';

test.describe('Feature Name', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('should do something', async ({ page }) => {
    await expect(page.getByRole('heading')).toBeVisible();
  });
});
```

### Page Object Model

```typescript
// pages/login.page.ts
import { Page, Locator } from '@playwright/test';

export class LoginPage {
  readonly page: Page;
  readonly usernameInput: Locator;
  readonly passwordInput: Locator;
  readonly submitButton: Locator;

  constructor(page: Page) {
    this.page = page;
    this.usernameInput = page.getByLabel('Username');
    this.passwordInput = page.getByLabel('Password');
    this.submitButton = page.getByRole('button', { name: 'Sign in' });
  }

  async goto() {
    await this.page.goto('/login');
  }

  async login(username: string, password: string) {
    await this.usernameInput.fill(username);
    await this.passwordInput.fill(password);
    await this.submitButton.click();
  }
}
```

### Using Page Objects in Tests

```typescript
import { test, expect } from '@playwright/test';
import { LoginPage } from '../pages/login.page';

test('user can login', async ({ page }) => {
  const loginPage = new LoginPage(page);
  await loginPage.goto();
  await loginPage.login('testuser', 'password123');
  await expect(page).toHaveURL('/dashboard');
});
```

## Running Tests

```bash
# Local browsers
npx playwright test

# Specific test file
npx playwright test tests/login.spec.ts

# With UI mode (local only)
npx playwright test --ui

# Headed mode (required for pinwiz.ai Cloudflare OTP gate)
npx playwright test --headed

# Specific browser
npx playwright test --project=chromium
```

## CI/CD Integration

### GitHub Actions

```yaml
- name: Install Playwright browsers
  run: npx playwright install --with-deps chromium

- name: Run Playwright tests
  run: npx playwright test
  env:
    TEST_BASE_URL: ${{ vars.TEST_BASE_URL }}

- name: Upload Playwright report
  uses: actions/upload-artifact@v4
  if: always()
  with:
    name: playwright-report
    path: playwright-report/
    retention-days: 30
```

## Troubleshooting

### Element not found
1. Use `await expect(locator).toBeVisible()` before interacting
2. Check for iframes: `page.frameLocator()`
3. Add explicit waits: `await page.waitForLoadState('networkidle')`
4. Use `pressSequentially` instead of `fill` for MudBlazor `MudTextField` inputs

### Cloudflare OTP gate blocks headless browser
- Use headed Edge browser (`--headed --project=edge`)
- Use `storageState` to cache authentication and skip the gate on subsequent runs
- See memory `reference_pinwiz_smoke_automation` for the full Gmail OTP handshake pattern

### Tests timeout
1. Check network connectivity to the target URL
2. Reduce worker count: `--workers=1`
3. Verify `TEST_BASE_URL` is reachable

## Documentation Links

- [Playwright Docs](https://playwright.dev/docs/intro)

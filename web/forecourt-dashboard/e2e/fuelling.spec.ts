import { expect, test } from '@playwright/test';

/**
 * One end-to-end fuelling, driven entirely through the UI (CLAUDE.md Phase 6).
 *
 * Nothing here is stubbed. Presenting the card runs the EMV kernel against the virtual card;
 * the approval goes through the transaction service and the acquirer; the dispenser that meters
 * the fuel is the real pump firmware, reached over OFP-1; and the trace the drawer shows was
 * captured by those components as they worked.
 */
test('a customer fuels at pump 1 and the trace shows the whole exchange', async ({ page }) => {
  await page.goto('/');

  const tile = page.getByTestId('pump-1');
  await expect(tile).toBeVisible();
  await expect(page.getByTestId('link-status')).toHaveText('connected');

  // The firmware link must be up before the pump can be authorised.
  await expect(tile.getByText('LINK Connected')).toBeVisible();

  // --- present a card at the terminal ---------------------------------------------------
  await page.getByTestId('card-pump').selectOption('1');
  await page.getByTestId('card-profile').selectOption('contactless-mc');
  await page.getByTestId('card-entry').selectOption('Contactless');
  await page.getByTestId('card-amount').fill('4000');
  await page.getByTestId('card-present').click();

  await expect(tile.getByTestId('pump-state')).toHaveText('Authorised');

  // --- lift the nozzle and let fuel flow -------------------------------------------------
  await page.getByTestId('sim-pump').selectOption('1');
  await page.getByTestId('sim-nozzle-up').click();

  await expect(tile.getByTestId('pump-state')).toHaveText('Dispensing');
  await expect
    .poll(async () => Number(await tile.getByTestId('pump-volume').innerText()), { timeout: 20_000 })
    .toBeGreaterThan(0.5);

  // --- holster, which ends the dispense and settles the delivered value ------------------
  await page.getByTestId('sim-nozzle-down').click();

  const row = page.getByTestId('tx-row').first();
  await expect(row).toContainText('Completed', { timeout: 30_000 });
  await expect(page.getByTestId('totals-completed')).not.toHaveText('0');

  // --- inspect the trace -----------------------------------------------------------------
  await row.click();
  const drawer = page.getByTestId('trace-drawer');
  await expect(drawer).toBeVisible();

  // The card exchange, the acquirer exchange and the dispenser commands are all present.
  await expect(page.getByTestId('trace-tab-apdu')).not.toContainText('(0)');
  await expect(page.getByTestId('trace-tab-pump')).not.toContainText('(0)');
  await expect(drawer.getByText('SELECT', { exact: false }).first()).toBeVisible();

  // Filtering to one kind narrows the list rather than reloading anything.
  await page.getByTestId('trace-tab-apdu').click();
  await expect(page.getByTestId('trace-step').first()).toBeVisible();

  // On the acquirer messages the PAN is masked by the dialect's own metadata. (The APDU tab
  // deliberately shows raw card bytes: that is inside the OPT boundary, on test PANs only.)
  await page.getByTestId('trace-tab-iso8583').click();
  await expect(drawer).toContainText('411111******1111');
  await expect(drawer).not.toContainText('4111111111111111');
});

import { expect, test } from '@playwright/test';

async function installModelContext(page, failTool = '') {
  await page.addInitScript(({ failTool }) => {
    const activeTools = new Map();
    const registrationCount = new Map();

    const modelContext = {
      registerTool(tool, options = {}) {
        if (tool.name === failTool) {
          return Promise.reject(new DOMException(`Registration failed for ${tool.name}`, 'NotAllowedError'));
        }
        if (activeTools.has(tool.name)) {
          return Promise.reject(new DOMException(`Tool ${tool.name} already exists`, 'InvalidStateError'));
        }

        activeTools.set(tool.name, tool);
        registrationCount.set(tool.name, (registrationCount.get(tool.name) ?? 0) + 1);
        options.signal?.addEventListener(
          'abort',
          () => {
            if (activeTools.get(tool.name) === tool) {
              activeTools.delete(tool.name);
            }
          },
          { once: true },
        );
        return Promise.resolve();
      },
      getTools() {
        return Promise.resolve(
          [...activeTools.values()].sort((left, right) => left.name.localeCompare(right.name)),
        );
      },
      executeTool(tool, jsonArguments) {
        return Promise.resolve(
          tool.execute(JSON.parse(jsonArguments), { signal: new AbortController().signal }),
        );
      },
      registrationCount,
      activeTools,
    };

    Object.defineProperty(Document.prototype, 'modelContext', {
      configurable: true,
      get: () => modelContext,
    });
  }, { failTool });
}

test('works normally without WebMCP support', async ({ page }) => {
  await page.goto('/');

  await expect(page.locator('.mcp-badge')).toContainText('Обычный режим');
  await page.getByRole('textbox', { name: 'Поиск' }).fill('OLED');
  await expect(page.locator('.product-card')).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'VectorBook Air 14' })).toBeVisible();
});

test('registers tools and applies agent actions to the UI', async ({ page }) => {
  await installModelContext(page);
  await page.goto('/');
  await expect(page.locator('.mcp-badge')).toContainText('3 WebMCP-инструмента');

  const registry = await page.evaluate(async () => ({
    names: (await document.modelContext.getTools()).map(({ name }) => name),
    registrations: Object.fromEntries(document.modelContext.registrationCount),
  }));

  expect(registry.names).toEqual(['add_to_cart', 'search_products', 'show_product']);
  expect(registry.registrations).toEqual({
    search_products: 2,
    show_product: 2,
    add_to_cart: 2,
  });

  const searchResult = await page.evaluate(async () => {
    const tools = await document.modelContext.getTools();
    const tool = tools.find(({ name }) => name === 'search_products');
    return document.modelContext.executeTool(
      tool,
      JSON.stringify({ category: 'audio', maxPrice: 10000, inStockOnly: true }),
    );
  });

  expect(searchResult.products).toHaveLength(1);
  expect(searchResult.products[0].id).toBe('orbit-buds');
  await expect(page.locator('.product-card')).toHaveCount(1);
  await expect(page.locator('select[name="category"]')).toHaveValue('audio');

  const addResult = await page.evaluate(async () => {
    const tools = await document.modelContext.getTools();
    const tool = tools.find(({ name }) => name === 'add_to_cart');
    return document.modelContext.executeTool(
      tool,
      JSON.stringify({ productId: 'orbit-buds', quantity: 2 }),
    );
  });

  expect(addResult.quantityInCart).toBe(2);
  await expect(page.getByRole('dialog', { name: /Корзина/ })).toContainText('13 980 ₽');

  await page.evaluate(async () => {
    const tools = await document.modelContext.getTools();
    const tool = tools.find(({ name }) => name === 'show_product');
    await document.modelContext.executeTool(
      tool,
      JSON.stringify({ productId: 'orbit-buds' }),
    );
  });

  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(page.getByRole('dialog', { name: 'Orbit Buds S' })).toBeVisible();
});

test('rolls back tools when registration is only partially successful', async ({ page }) => {
  await installModelContext(page, 'show_product');
  await page.goto('/');
  await expect(page.locator('.mcp-badge')).toContainText('WebMCP не запущен');

  const activeToolCount = await page.evaluate(() => document.modelContext.activeTools.size);
  expect(activeToolCount).toBe(0);
});

test('rejects unavailable stock with a useful error', async ({ page }) => {
  await installModelContext(page);
  await page.goto('/');
  await expect(page.locator('.mcp-badge')).toContainText('3 WebMCP-инструмента');

  const message = await page.evaluate(async () => {
    const tools = await document.modelContext.getTools();
    const tool = tools.find(({ name }) => name === 'add_to_cart');

    try {
      await document.modelContext.executeTool(
        tool,
        JSON.stringify({ productId: 'frame-monitor', quantity: 1 }),
      );
      return '';
    } catch (error) {
      return error.message;
    }
  });

  expect(message).toContain('Only 0 unit(s) of Frame 27 Studio are available');
});

test('fits a mobile viewport without horizontal overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');

  const sizes = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
  }));

  expect(sizes.scrollWidth).toBe(sizes.clientWidth);
});

test('returns focus after product and cart dialog flow', async ({ page }) => {
  await page.goto('/');
  const productButton = page.getByRole('button', { name: 'Открыть Orbit Buds S' });

  await productButton.click();
  await page.getByRole('dialog', { name: 'Orbit Buds S' }).getByRole('button', { name: 'Добавить в корзину' }).click();
  await page.getByRole('button', { name: 'Закрыть корзину' }).click();

  await expect(productButton).toBeFocused();
});

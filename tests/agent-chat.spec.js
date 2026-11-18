import { test, expect } from '@playwright/test';

test('assistant shows server preview and requires confirmation before draft creation', async ({ page }) => {
  await page.route('**/api/products?status=all', route => route.fulfill({ json: [] }));
  await page.route('**/api/agent/turns', route => route.fulfill({ json: {
    kind: 'confirmation_required', conversationId: 'conversation-1', actionId: 'action-1',
    message: 'Проверьте данные товара перед созданием черновика.',
    preview: { name: 'Наушники', category: 'audio', price: 1200, stock: 4, lowStockThreshold: 1, description: 'Тест', id: 'draft-1', status: 'draft', accent: '#a3e635', mark: 'NEW', tags: [] },
  } }));
  await page.route('**/api/agent/actions/action-1/decision', route => route.fulfill({ json: { kind: 'answer', conversationId: 'conversation-1', message: 'Черновик создан. Он не опубликован.' } }));
  await page.goto('/admin');
  await page.getByRole('button', { name: /Помощник/ }).click();
  await expect(page.getByRole('dialog', { name: 'Помощник' })).toBeVisible();
  await page.getByRole('button', { name: 'Создай черновик товара' }).click();
  await expect(page.getByText('Предпросмотр · черновик')).toBeVisible();
  await expect(page.getByText('Создать черновик')).toBeVisible();
  await page.getByRole('button', { name: 'Создать черновик' }).click();
  await expect(page.getByRole('paragraph').filter({ hasText: 'Черновик создан. Он не опубликован.' })).toBeVisible();
});

test('renders typed product lists and sales report summaries', async ({ page }) => {
  await page.route('**/api/products?status=all', route => route.fulfill({ json: [] }));
  await page.route('**/api/agent/turns', route => route.fulfill({ json: {
    kind: 'answer', conversationId: 'c1', message: 'Вот результаты.', results: [
      { type: 'products', items: [{ id: 'p1', name: 'Наушники', category: 'audio', price: 1200, availableStock: 3, status: 'published' }], totalMatches: 8, hasMore: true },
      { type: 'salesReport', report: { from: '2026-03-01', to: '2026-03-31', completedOrders: 12, revenue: 24500, totalProducts: 40, hasMore: true, products: [{ productId: 'p1', name: 'Наушники', quantity: 4, revenue: 4800 }] } },
    ],
  } }));
  await page.goto('/admin');
  await page.getByRole('button', { name: /Помощник/ }).click();
  await page.getByRole('button', { name: 'Покажи товары' }).click();
  await expect(page.getByText('Показано 1 из 8 · список сокращён')).toBeVisible();
  await expect(page.getByText(/1\s?200\s?₽/)).toBeVisible();
  await expect(page.getByText(/1 мар\. 2026/)).toBeVisible();
  await expect(page.getByText('12')).toBeVisible();
  await expect(page.getByText(/24\s?500\s?₽/)).toBeVisible();
  await expect(page.getByText('Продажи по товарам')).toBeVisible();
  await expect(page.getByText('Показано 1 из 40 товаров · список сокращён')).toBeVisible();
});

test('cancel decision is sent to the server and Escape restores trigger focus', async ({ page }) => {
  await page.route('**/api/products?status=all', route => route.fulfill({ json: [] }));
  await page.route('**/api/agent/turns', route => route.fulfill({ json: { kind: 'confirmation_required', conversationId: 'c1', actionId: 'a1', message: 'Preview', preview: { name: 'Item' } } }));
  let decision;
  await page.route('**/api/agent/actions/a1/decision', async route => { decision = route.request().postDataJSON(); await route.fulfill({ json: { kind: 'answer', conversationId: 'c1', message: 'Создание отменено.' } }); });
  await page.goto('/admin');
  const trigger = page.getByRole('button', { name: /Помощник/ });
  await trigger.click();
  await page.getByRole('button', { name: 'Создай черновик товара' }).click();
  await page.getByRole('button', { name: 'Отменить' }).click();
  await expect.poll(() => decision?.decision).toBe('reject');
  await expect(page.getByText('Создание отменено.')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Помощник' })).toBeHidden();
  await expect(trigger).toBeFocused();
});

test('shows pending status and a retryable message after a network error', async ({ page }) => {
  await page.route('**/api/products?status=all', route => route.fulfill({ json: [] }));
  let attempts = 0;
  await page.route('**/api/agent/turns', async route => {
    attempts++;
    if (attempts === 1) { await new Promise(resolve => setTimeout(resolve, 250)); await route.abort(); }
    else await route.fulfill({ json: { kind: 'answer', message: 'Ответ получен.', results: [] } });
  });
  await page.goto('/admin');
  await page.getByRole('button', { name: /Помощник/ }).click();
  await page.getByRole('textbox', { name: 'Сообщение' }).fill('Проверь каталог');
  await page.getByRole('button', { name: 'Отправить сообщение' }).click();
  await expect(page.getByRole('status')).toBeVisible();
  await expect(page.getByRole('alert')).toContainText('Не удалось связаться');
  await page.getByRole('button', { name: 'Повторить' }).click();
  await expect(page.getByText('Ответ получен.')).toBeVisible();
});

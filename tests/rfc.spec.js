import { expect, test } from '@playwright/test';
import {
  apiJson,
  apiRequest,
  archiveProduct,
  assertStockInvariants,
  createCart,
  createProduct,
  orderAction,
  orders,
  products,
  publishProduct,
  seedOrder,
  seedProduct,
  submitCart,
  uniqueId,
} from './rfc.helpers.js';

test.describe('RFC acceptance', () => {
  test('starts with the seeded catalog, order history, and valid stock invariants', async ({ page, request }) => {
    const allProducts = await products(request);
    const seedProducts = allProducts.filter(seedProduct);
    expect(seedProducts).toHaveLength(40);
    expect(seedProducts.every((product) => product.status === 'published')).toBe(true);

    const completed = (await orders(request, 'status=completed')).filter(seedOrder);
    expect(completed).toHaveLength(50);
    const productIds = new Set(seedProducts.map((product) => product.id));
    for (const order of completed) {
      expect(order.items.length).toBeGreaterThan(0);
      for (const item of order.items) expect(productIds.has(item.productId)).toBe(true);
    }
    assertStockInvariants(allProducts);

    await page.goto('/admin');
    await expect(page.getByRole('heading', { name: 'Контур магазина' })).toBeVisible();
    await expect(page.getByText(/API доступен/)).toBeVisible();
  });

  test('covers product lifecycle and atomic bulk operations', async ({ page, request }) => {
    const lifecycle = await createProduct(request, { id: uniqueId('lifecycle') });
    const id = lifecycle.body.id;

    expect((await products(request, 'published')).some((product) => product.id === id)).toBe(false);
    await page.goto('/admin');
    await page.getByPlaceholder('Поиск по названию, ID, тегам').fill(id);
    await expect(page.getByText(lifecycle.body.name, { exact: true })).toBeVisible();

    await publishProduct(request, id);
    expect((await products(request, 'published')).find((product) => product.id === id)).toMatchObject({ status: 'published' });

    const updated = { ...lifecycle.body, name: `${lifecycle.body.name} updated`, price: 123457, stock: 9, lowStockThreshold: 3, tags: ['changed'] };
    const update = await apiJson(request, `/products/${id}`, { method: 'PUT', body: updated });
    expect(update).toMatchObject({ id, status: 'published', name: updated.name, price: updated.price, stock: updated.stock, tags: updated.tags });

    await archiveProduct(request, id);
    expect((await products(request, 'published')).some((product) => product.id === id)).toBe(false);
    await publishProduct(request, id);
    expect((await products(request, 'published')).some((product) => product.id === id)).toBe(true);

    const bulkProducts = [
      await createProduct(request, { id: uniqueId('bulk') }),
      await createProduct(request, { id: uniqueId('bulk') }),
    ];
    const bulkIds = bulkProducts.map(({ body }) => body.id);
    expect((await apiJson(request, '/products/bulk', { method: 'POST', body: { ids: bulkIds, action: 'publish' } })).products)
      .toHaveLength(2);
    const categoryResult = await apiJson(request, '/products/bulk', { method: 'POST', body: { ids: bulkIds, action: 'category', category: 'audio' } });
    expect(categoryResult.products.every((product) => product.category === 'audio')).toBe(true);
    const stockResult = await apiJson(request, '/products/bulk', { method: 'POST', body: { ids: bulkIds, action: 'stock', stock: 7 } });
    expect(stockResult.products.every((product) => product.stock === 7)).toBe(true);
    const archived = await apiJson(request, '/products/bulk', { method: 'POST', body: { ids: bulkIds, action: 'archive' } });
    expect(archived.products.every((product) => product.status === 'archived')).toBe(true);
  });

  test('applies low-stock and storefront availability filters', async ({ page, request }) => {
    const low = await createProduct(request, { id: uniqueId('low'), stock: 1, lowStockThreshold: 2 });
    const empty = await createProduct(request, { id: uniqueId('empty'), stock: 0, lowStockThreshold: 1 });
    await publishProduct(request, low.body.id);
    await publishProduct(request, empty.body.id);

    const visible = await products(request, 'published');
    const lowProduct = visible.find((product) => product.id === low.body.id);
    const emptyProduct = visible.find((product) => product.id === empty.body.id);
    expect(lowProduct.availableStock).toBe(1);
    expect(lowProduct.availableStock).toBeLessThanOrEqual(lowProduct.lowStockThreshold);
    expect(emptyProduct.availableStock).toBe(0);

    await page.goto('/admin');
    const availability = page.locator('.admin-filters').first().getByRole('combobox').nth(2);
    await availability.selectOption('low');
    await expect(page.getByText(low.body.name, { exact: true })).toBeVisible();
    await expect(page.getByText(empty.body.name, { exact: true })).toBeVisible();

    await page.goto('/');
    const search = page.getByRole('textbox', { name: 'Поиск' });
    await search.fill(low.body.id);
    await expect(page.locator('.product-card')).toHaveCount(1);
    const inStock = page.locator('.filters input[type="checkbox"]');
    await inStock.check();
    await expect(page.locator('.product-card')).toHaveCount(1);
    await search.fill(empty.body.id);
    await expect(page.locator('.product-card')).toHaveCount(0);
  });

  test('reserves, completes, and cancels carts without violating stock invariants', async ({ page, request }) => {
    const product = await createProduct(request, { id: uniqueId('stock-flow'), stock: 8 });
    await publishProduct(request, product.body.id);
    const initial = (await products(request, 'all')).find((item) => item.id === product.body.id);

    const openCart = await createCart(request, [{ productId: product.body.id, quantity: 2 }]);
    const afterOpen = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(openCart.status).toBe('open_cart');
    expect(afterOpen).toMatchObject({ stock: initial.stock, reservedStock: 0 });

    const submitted = await submitCart(request, openCart.id);
    expect(submitted.status).toBe('submitted');
    const afterReserve = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(afterReserve).toMatchObject({ stock: initial.stock, reservedStock: 2, availableStock: initial.stock - 2 });
    const completed = await orderAction(request, submitted.id, 'complete');
    expect(completed.status).toBe('completed');
    const afterComplete = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(afterComplete).toMatchObject({ stock: initial.stock - 2, reservedStock: 0, availableStock: initial.stock - 2 });

    const adminCancel = await submitCart(request, await createCart(request, [{ productId: product.body.id, quantity: 1 }]).then((cart) => cart.id));
    expect(adminCancel.status).toBe('submitted');
    expect((await orderAction(request, adminCancel.id, 'cancel')).status).toBe('cancelled');
    const afterAdminCancel = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(afterAdminCancel).toMatchObject({ stock: initial.stock - 2, reservedStock: 0 });

    const userCart = await createCart(request, [{ productId: product.body.id, quantity: 1 }]);
    const userOrder = await submitCart(request, userCart.id);
    await page.goto('/');
    await page.getByRole('button', { name: 'Мои заказы' }).click();
    const orderCard = page.locator('.order-card').filter({ hasText: userOrder.id });
    await expect(orderCard).toBeVisible();
    await orderCard.getByRole('button', { name: 'Отменить заказ' }).click();
    await expect(orderCard).toContainText('Отменён');
    const afterUserCancel = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(afterUserCancel).toMatchObject({ stock: initial.stock - 2, reservedStock: 0 });
    assertStockInvariants(await products(request, 'all'));
  });

  test('keeps abandoned/open carts harmless and filters archived order history', async ({ page, request }) => {
    const product = await createProduct(request, { id: uniqueId('history'), stock: 4 });
    await publishProduct(request, product.body.id);
    const open = await createCart(request, [{ productId: product.body.id, quantity: 1 }]);
    const before = (await products(request, 'all')).find((item) => item.id === product.body.id);
    const openOrders = await orders(request, `productId=${product.body.id}&status=open_cart`);
    expect(openOrders.some((order) => order.id === open.id && order.status === 'open_cart')).toBe(true);
    const abandonedOrders = await orders(request, 'status=abandoned');
    expect(abandonedOrders.every((order) => order.status === 'abandoned')).toBe(true);
    expect(abandonedOrders.some((order) => order.id === open.id)).toBe(false);
    expect((await products(request, 'all')).find((item) => item.id === product.body.id)).toMatchObject({ stock: before.stock, reservedStock: 0 });

    const submitted = await submitCart(request, open.id);
    await archiveProduct(request, product.body.id);
    const filteredSubmitted = await orders(request, `productId=${product.body.id}&status=submitted`);
    expect(filteredSubmitted).toHaveLength(1);
    expect(filteredSubmitted[0]).toMatchObject({ id: submitted.id, status: 'submitted' });
    expect(filteredSubmitted[0].items[0]).toMatchObject({ productId: product.body.id, name: product.body.name });

    await orderAction(request, submitted.id, 'complete');
    const filteredCompleted = await orders(request, `productId=${product.body.id}&status=completed`);
    expect(filteredCompleted).toHaveLength(1);
    expect(filteredCompleted[0].items[0].name).toBe(product.body.name);
    await page.goto('/admin');
    await page.getByRole('button', { name: /Корзины и заказы/ }).click();
    await expect(page.getByText(`#${submitted.id}`, { exact: true })).toBeVisible();
  });

  test('reports completed sales and preserves stock invariants', async ({ request }) => {
    const product = await createProduct(request, { id: uniqueId('report'), stock: 6, price: 3210 });
    await publishProduct(request, product.body.id);
    const period = 'from=2026-01-01&to=2026-12-31';
    const before = await apiJson(request, `/reports/sales?${period}`);
    const cart = await createCart(request, [{ productId: product.body.id, quantity: 2 }]);
    const submitted = await submitCart(request, cart.id);
    const during = await apiJson(request, `/reports/sales?${period}`);
    expect(during).toMatchObject({ completedOrders: before.completedOrders, revenue: before.revenue });
    await orderAction(request, submitted.id, 'complete');
    const after = await apiJson(request, `/reports/sales?${period}`);
    expect(after.completedOrders).toBe(before.completedOrders + 1);
    expect(after.revenue).toBe(before.revenue + product.body.price * 2);
    expect(after.products.find((item) => item.productId === product.body.id)).toMatchObject({ quantity: 2, revenue: product.body.price * 2 });
    const finalProduct = (await products(request, 'all')).find((item) => item.id === product.body.id);
    expect(finalProduct).toMatchObject({ stock: 4, reservedStock: 0, availableStock: 4 });
    assertStockInvariants(await products(request, 'all'));
  });
});

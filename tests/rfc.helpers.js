import { expect } from '@playwright/test';

export const API_URL = 'http://127.0.0.1:5080/api';

let sequence = 0;

export function uniqueId(prefix = 'e2e') {
  sequence += 1;
  return `${prefix}-${Date.now().toString(36)}-${sequence}`.toLowerCase();
}

export function productBody(id = uniqueId()) {
  return {
    id,
    name: `E2E ${id}`,
    category: 'workspace',
    price: 123456,
    stock: 12,
    lowStockThreshold: 2,
    accent: '#123456',
    mark: 'E2E',
    description: `Playwright product ${id}`,
    tags: ['e2e', 'automation'],
  };
}

export async function apiRequest(request, path, options = {}) {
  const response = await request.fetch(`${API_URL}${path}`, {
    method: options.method || 'GET',
    data: options.body,
    headers: options.body ? { 'Content-Type': 'application/json' } : undefined,
  });
  const body = await response.json().catch(() => null);
  return { response, body };
}

export async function apiJson(request, path, options = {}) {
  const result = await apiRequest(request, path, options);
  if (!result.response.ok()) {
    throw new Error(`${options.method || 'GET'} ${path} failed (${result.response.status()}): ${JSON.stringify(result.body)}`);
  }
  return result.body;
}

export async function createProduct(request, overrides = {}) {
  const body = { ...productBody(), ...overrides };
  const product = await apiJson(request, '/products', { method: 'POST', body });
  expect(product).toMatchObject({ id: body.id, status: 'draft' });
  return { product, body };
}

export async function publishProduct(request, id) {
  return apiJson(request, `/products/${encodeURIComponent(id)}/publish`, { method: 'POST' });
}

export async function archiveProduct(request, id) {
  return apiJson(request, `/products/${encodeURIComponent(id)}/archive`, { method: 'POST' });
}

export async function createCart(request, items) {
  return apiJson(request, '/carts', { method: 'POST', body: { items } });
}

export async function submitCart(request, id) {
  return apiJson(request, `/carts/${id}/submit`, { method: 'POST' });
}

export async function orderAction(request, id, action) {
  return apiJson(request, `/orders/${id}/${action}`, { method: 'POST' });
}

export async function products(request, status = 'all') {
  return apiJson(request, `/products?status=${encodeURIComponent(status)}`);
}

export async function orders(request, query = '') {
  return apiJson(request, `/orders${query ? `?${query}` : ''}`);
}

export function seedProduct(product) {
  return ['orbit-buds', 'vector-air', 'pulse-watch', 'frame-monitor', 'key-one', 'beam-speaker'].includes(product.id)
    || /^signal-\d{2}$/.test(product.id);
}

export function seedOrder(order) {
  return /^seed-order-\d{3}$/.test(order.id);
}

export function assertStockInvariants(productList) {
  for (const product of productList) {
    expect(product.stock).toBeGreaterThanOrEqual(0);
    expect(product.reservedStock).toBeGreaterThanOrEqual(0);
    expect(product.reservedStock).toBeLessThanOrEqual(product.stock);
    expect(product.availableStock).toBe(product.stock - product.reservedStock);
  }
}

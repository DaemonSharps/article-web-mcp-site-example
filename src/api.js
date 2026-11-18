const json = async (path, options = {}) => {
  const response = await fetch(`/api${path}`, { headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }, ...options });
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.message || body?.error || `Запрос не выполнен (${response.status})`);
  return body;
};

export const api = {
  products: (all = false) => json(`/products${all ? '?status=all' : ''}`),
  createProduct: (product) => json('/products', { method: 'POST', body: JSON.stringify(product) }),
  updateProduct: (id, product) => json(`/products/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(product) }),
  productAction: (id, action) => json(`/products/${encodeURIComponent(id)}/${action}`, { method: 'POST' }),
  bulk: (body) => json('/products/bulk', { method: 'POST', body: JSON.stringify(body) }),
  orders: (params = {}) => json(`/orders?${new URLSearchParams(Object.entries(params).filter(([, value]) => value))}`),
  myOrders: () => json('/my-orders'),
  createCart: (items) => json('/carts', { method: 'POST', body: JSON.stringify({ items }) }),
  submitCart: (id) => json(`/carts/${id}/submit`, { method: 'POST' }),
  orderAction: (id, action) => json(`/orders/${id}/${action}`, { method: 'POST' }),
  report: (from, to) => json(`/reports/sales?${new URLSearchParams({ from, to })}`),
  agentTurn: (body) => json('/agent/turns', { method: 'POST', body: JSON.stringify(body) }),
  agentDecision: (actionId, body) => json(`/agent/actions/${encodeURIComponent(actionId)}/decision`, { method: 'POST', body: JSON.stringify(body) }),
};

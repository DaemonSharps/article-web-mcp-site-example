import { expect, test } from '@playwright/test';
import { createServer } from 'node:http';

const fakeModelPort = Number(process.env.AGENT_TEST_MODEL_PORT ?? 8080);

async function startFakeModel(handler) {
  const server = createServer(handler);
  return new Promise((resolve) => {
    server.once('error', () => resolve(null));
    server.listen(fakeModelPort, '127.0.0.1', () => resolve(server));
  });
}

test.describe('local agent API boundary', () => {
  test('rejects malformed turns with the typed error shape', async ({ request }) => {
    const response = await request.post('http://127.0.0.1:5080/api/agent/turns', {
      data: { requestId: '', message: 'hello' },
    });
    expect(response.status()).toBe(400);
    expect(await response.json()).toMatchObject({ kind: 'error', code: 'invalid_request' });
  });

  test('does not accept a decision without its server-owned pending action', async ({ request }) => {
    const response = await request.post('http://127.0.0.1:5080/api/agent/actions/not-real/decision', {
      data: { conversationId: 'unknown-conversation', requestId: 'decision-1', decision: 'confirm' },
    });
    expect(response.status()).toBe(200);
    expect(await response.json()).toMatchObject({ kind: 'error', code: 'action_not_found' });
  });

  test('sales report reads do not alter open cart state', async ({ request }) => {
    const before = await request.get('http://127.0.0.1:5080/api/orders?status=open_cart');
    const report = await request.get('http://127.0.0.1:5080/api/reports/sales?from=2026-01-01&to=2026-12-31');
    const after = await request.get('http://127.0.0.1:5080/api/orders?status=open_cart');
    expect(report.ok()).toBe(true);
    expect(await after.json()).toEqual(await before.json());
  });

  test('returns bounded typed product summaries for status=all and reuses a requestId', async ({ request }) => {
    let completionCount = 0;
    const tokenBudgets = [];
    const fakeModel = createServer(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      completionCount += 1;
      tokenBudgets.push(body.max_tokens);
      if (completionCount === 1) {
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
          id: 'test-products-call', type: 'function', function: { name: 'list_products', arguments: '{"status":"all"}' },
        }] } }] }));
      } else {
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: 'Catalog results are ready.' } }] }));
      }
    });

    try {
      await new Promise((resolve, reject) => {
        fakeModel.once('error', reject);
        fakeModel.listen(fakeModelPort, '127.0.0.1', resolve);
      });
    } catch {
      test.skip(true, 'Port 8080 is already occupied; skipping deterministic local-model stub test');
      return;
    }

    try {
      const draftResponse = await request.post('http://127.0.0.1:5080/api/products', { data: {
        id: 'agent-all-status-test', name: 'Agent all-status test', category: 'audio', price: 1111, stock: 3,
        lowStockThreshold: 1, status: 'draft', accent: '#a3e635', mark: 'AT', description: 'Test-only draft product', tags: [],
      } });
      expect(draftResponse.status()).toBe(201);
      const allProducts = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      const turn = { requestId: 'bounded-products-test', message: 'List all catalog products' };
      const firstResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', { data: turn });
      expect(firstResponse.ok()).toBe(true);
      const first = await firstResponse.json();
      expect(first).toMatchObject({
        kind: 'answer',
        message: 'Каталог: найдено товаров — 41, показано — 8.',
        results: [{ type: 'products', hasMore: true }],
      });
      expect(first.results[0].totalMatches).toBeGreaterThan(8);
      expect(first.results[0].totalMatches).toBe(allProducts.length);
      expect(first.results[0].items).toHaveLength(8);
      expect(first.results[0].items.find((product) => product.id === 'agent-all-status-test')).toMatchObject({ status: 'draft' });
      for (const product of first.results[0].items) {
        expect(product).toHaveProperty('availableStock');
        expect(product).not.toHaveProperty('description');
        expect(product).not.toHaveProperty('tags');
      }
      expect(tokenBudgets).toEqual([1024]);

      const duplicateResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { ...turn, conversationId: first.conversationId },
      });
      expect(await duplicateResponse.json()).toEqual(first);
      expect(completionCount).toBe(1);
    } finally {
      await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('returns canonical sales summary and a top-ten revenue breakdown in one completion', async ({ request }) => {
    let completionCount = 0;
    const tokenBudgets = [];
    const fakeModel = createServer(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      completionCount += 1;
      tokenBudgets.push(body.max_tokens);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
        id: 'test-sales-call', type: 'function', function: { name: 'get_sales_report', arguments: '{"from":"2026-01-01","to":"2026-12-31"}' },
      }] } }] }));
    });

    try {
      await new Promise((resolve, reject) => {
        fakeModel.once('error', reject);
        fakeModel.listen(fakeModelPort, '127.0.0.1', resolve);
      });
    } catch {
      test.skip(true, 'Configured fake-model port is already occupied; skipping deterministic local-model stub test');
      return;
    }

    try {
      const reportUrl = 'http://127.0.0.1:5080/api/reports/sales?from=2026-01-01&to=2026-12-31';
      const canonicalBefore = await (await request.get(reportUrl)).json();
      const response = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'canonical-sales-summary', message: 'Show me the sales report for 2026.' },
      });
      const answer = await response.json();
      const canonicalAfter = await (await request.get(reportUrl)).json();
      expect(response.ok()).toBe(true);
      expect(answer).toMatchObject({ kind: 'answer', results: [{ type: 'salesReport' }] });
      const sales = answer.results[0].report;
      expect(sales).toMatchObject({
        completedOrders: canonicalBefore.completedOrders,
        revenue: canonicalBefore.revenue,
        totalProducts: canonicalBefore.products.length,
        hasMore: canonicalBefore.products.length > 10,
      });
      expect(sales.products).toHaveLength(Math.min(10, canonicalBefore.products.length));
      const expectedTopTen = [...canonicalBefore.products]
        .sort((a, b) => b.revenue - a.revenue || (a.name < b.name ? -1 : a.name > b.name ? 1 : 0)).slice(0, 10);
      expect(sales.products.map((product) => product.productId)).toEqual(expectedTopTen.map((product) => product.productId));
      const formattedRevenue = canonicalBefore.revenue.toLocaleString('en-US').replaceAll(',', ' ');
      expect(answer.message).toBe(`Отчёт по продажам за период 2026-01-01 — 2026-12-31: завершённых заказов — ${canonicalBefore.completedOrders}, выручка — ${formattedRevenue} ₽.`);
      expect(canonicalAfter).toEqual(canonicalBefore);
      expect(completionCount).toBe(1);
      expect(tokenBudgets).toEqual([1024]);
    } finally {
      await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('clarifies an unsupported draft category without creating a product or pending action', async ({ request }) => {
    let completionCount = 0;
    let createDraftSchema;
    let followupMessages = [];
    const fakeModel = createServer(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      completionCount += 1;
      if (completionCount === 1) {
        createDraftSchema = body.tools.find((tool) => tool.function.name === 'create_product_draft').function.parameters.properties.category;
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
          id: 'test-invalid-category-call', type: 'function', function: { name: 'create_product_draft', arguments: JSON.stringify({
            name: 'Orbit probe headphones', category: 'electronics', price: 6990, stock: 12, lowStockThreshold: 3, description: 'Wireless headphones for the probe test.',
          }) },
        }] } }] }));
      } else {
        followupMessages = body.messages;
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
          id: 'test-valid-category-followup', type: 'function', function: { name: 'create_product_draft', arguments: '{"category":"audio"}' },
        }] } }] }));
      }
    });

    try {
      await new Promise((resolve, reject) => {
        fakeModel.once('error', reject);
        fakeModel.listen(fakeModelPort, '127.0.0.1', resolve);
      });
    } catch {
      test.skip(true, 'Port 8080 is already occupied; skipping deterministic local-model stub test');
      return;
    }

    try {
      const before = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      const firstResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'invalid-category-draft', message: 'Create a new product draft: wireless Orbit headphones.' },
      });
      const first = await firstResponse.json();
      expect(first).toMatchObject({ kind: 'clarification', missingFields: ['category'] });
      expect(first).not.toHaveProperty('actionId');
      expect(first).not.toHaveProperty('preview');
      expect(createDraftSchema.enum).toEqual(['audio', 'computers', 'wearables', 'workspace']);
      expect(createDraftSchema.description).toContain('Do not infer');

      const after = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      expect(after).toEqual(before);

      const followup = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { conversationId: first.conversationId, requestId: 'invalid-category-followup', message: 'I will choose audio.' },
      });
      expect(await followup.json()).toMatchObject({ kind: 'confirmation_required', preview: { category: 'audio' } });
      const toolReply = followupMessages.find((message) => message.role === 'tool' && message.tool_call_id === 'test-invalid-category-call');
      expect(JSON.parse(toolReply.content)).toMatchObject({ kind: 'clarification', missingFields: ['category'] });
      expect(completionCount).toBe(2);
    } finally {
      await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('forces draft tool for explicit intent and collects missing values on follow-up', async ({ request }) => {
    let completionCount = 0;
    const toolChoices = [];
    const toolCounts = [];
    const fakeModel = await startFakeModel(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      completionCount += 1;
      toolChoices.push(body.tool_choice);
      toolCounts.push(body.tools.length);
      const args = completionCount === 1
        ? { name: 'Forced Draft Test Headphones' }
        : { category: 'audio', price: 5490, stock: 7, lowStockThreshold: 2, description: 'Wireless headphones for the forced tool test.' };
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
        id: `forced-draft-${completionCount}`, type: 'function', function: { name: 'create_product_draft', arguments: JSON.stringify(args) },
      }] } }] }));
    });
    test.skip(!fakeModel, 'Configured fake-model port is occupied; cannot run deterministic draft flow');

    try {
      const before = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      const firstResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'forced-draft-start', message: 'Создай черновик нового товара: беспроводные наушники Orbit.' },
      });
      const first = await firstResponse.json();
      expect(first).toMatchObject({ kind: 'clarification', missingFields: ['category', 'price', 'stock', 'lowStockThreshold', 'description'] });
      expect(toolChoices[0]).toBe('required');
      expect(toolCounts[0]).toBe(1);

      const followupResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { conversationId: first.conversationId, requestId: 'forced-draft-followup', message: 'Категория audio, цена 5490, остаток 7, порог 2. Описание: беспроводные наушники.' },
      });
      const followup = await followupResponse.json();
      expect(followup).toMatchObject({
        kind: 'confirmation_required',
        preview: { name: 'Forced Draft Test Headphones', category: 'audio', price: 5490, stock: 7, lowStockThreshold: 2, status: 'draft' },
      });
      expect(toolChoices).toEqual(['required', 'required']);
      expect(toolCounts).toEqual([1, 1]);
      const after = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      expect(after).toEqual(before);
      expect(completionCount).toBe(2);
    } finally {
      if (fakeModel) await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('never surfaces model prose when a forced draft completion omits tool calls', async ({ request }) => {
    let completionCount = 0;
    let toolChoice;
    const fakeModel = await startFakeModel(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      toolChoice = body.tool_choice;
      completionCount += 1;
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: 'Internal narration <response>create a draft</response>' } }] }));
    });
    test.skip(!fakeModel, 'Configured fake-model port is occupied; cannot run deterministic prose-fallback test');

    try {
      const response = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'forced-draft-prose-fallback', message: 'Create a new product draft for Orbit headphones.' },
      });
      const result = await response.json();
      expect(result.kind).toBe('clarification');
      expect(result.missingFields).toEqual(['name', 'category', 'price', 'stock', 'lowStockThreshold', 'description']);
      expect(result.message).not.toContain('<response>');
      expect(result.message).not.toContain('Internal narration');
      expect(result).not.toHaveProperty('actionId');
      expect(toolChoice).toBe('required');
      expect(completionCount).toBe(1);
    } finally {
      if (fakeModel) await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('cancels an incomplete draft without inference or database writes', async ({ request }) => {
    let completionCount = 0;
    const fakeModel = await startFakeModel(async (req, res) => {
      for await (const _chunk of req) { /* consume request body */ }
      completionCount += 1;
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ choices: [{ finish_reason: 'tool_calls', message: { role: 'assistant', tool_calls: [{
        id: 'cancel-draft-call', type: 'function', function: { name: 'create_product_draft', arguments: '{"name":"Cancel Draft Probe"}' },
      }] } }] }));
    });
    test.skip(!fakeModel, 'Configured fake-model port is occupied; cannot run deterministic cancel test');

    try {
      const before = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      const initialResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'cancel-draft-start', message: 'Create a draft product named Cancel Draft Probe.' },
      });
      const initial = await initialResponse.json();
      expect(initial.kind).toBe('clarification');
      const canceledResponse = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { conversationId: initial.conversationId, requestId: 'cancel-draft-now', message: 'Отмени создание черновика.' },
      });
      expect(await canceledResponse.json()).toMatchObject({ kind: 'answer', message: 'Создание черновика отменено. Запись не создана.' });
      expect(await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json()).toEqual(before);
      expect(completionCount).toBe(1);
    } finally {
      if (fakeModel) await new Promise((resolve) => fakeModel.close(resolve));
    }
  });

  test('does not force the draft tool for negated creation intent', async ({ request }) => {
    let completionCount = 0;
    let toolChoice;
    let toolCount;
    const fakeModel = await startFakeModel(async (req, res) => {
      const chunks = [];
      for await (const chunk of req) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      completionCount += 1;
      toolChoice = body.tool_choice;
      toolCount = body.tools.length;
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: 'Понял, черновик создавать не буду.' } }] }));
    });
    test.skip(!fakeModel, 'Configured fake-model port is occupied; cannot run deterministic negation test');

    try {
      const before = await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json();
      const response = await request.post('http://127.0.0.1:5080/api/agent/turns', {
        data: { requestId: 'negated-draft-intent', message: 'Не создавай черновик товара Orbit.' },
      });
      expect(await response.json()).toMatchObject({ kind: 'answer', message: 'Понял, черновик создавать не буду.' });
      expect(toolChoice).toBe('auto');
      expect(toolCount).toBe(3);
      expect(await (await request.get('http://127.0.0.1:5080/api/products?status=all')).json()).toEqual(before);
      expect(completionCount).toBe(1);
    } finally {
      if (fakeModel) await new Promise((resolve) => fakeModel.close(resolve));
    }
  });
});

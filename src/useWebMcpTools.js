import { useEffect, useLayoutEffect, useRef, useState } from 'react';

const productIdSchema = {
  type: 'string',
  description: 'Stable product identifier returned by search_products.',
};

function createSearchProductsTool(handlersRef) {
  return {
    name: 'search_products',
    description: 'Filter and display electronics in the current store catalog. Returns matching products with stable ids, prices and stock.',
    inputSchema: {
      type: 'object',
      properties: {
        query: {
          type: 'string',
          description: 'Optional words from a product name, description or feature.',
        },
        category: {
          type: 'string',
          enum: ['all', 'audio', 'computers', 'wearables', 'workspace'],
          description: 'Product category. Use all when no category is requested.',
        },
        maxPrice: {
          type: 'number',
          minimum: 0,
          description: 'Optional maximum price in Russian rubles.',
        },
        inStockOnly: {
          type: 'boolean',
          description: 'Whether to hide products that are out of stock.',
        },
      },
    },
    annotations: {
      readOnlyHint: false,
      untrustedContentHint: false,
    },
    execute: (args, options) => handlersRef.current.searchProducts(args, options),
  };
}

function createShowProductTool(handlersRef) {
  return {
    name: 'show_product',
    description: 'Open a product card in the current page. Use an id returned by search_products.',
    inputSchema: {
      type: 'object',
      properties: { productId: productIdSchema },
      required: ['productId'],
    },
    annotations: {
      readOnlyHint: false,
      untrustedContentHint: false,
    },
    execute: (args, options) => handlersRef.current.showProduct(args, options),
  };
}

function createAddToCartTool(handlersRef) {
  return {
    name: 'add_to_cart',
    description: 'Add an available product to the visible shopping cart. This changes cart state but does not place or pay for an order.',
    inputSchema: {
      type: 'object',
      properties: {
        productId: productIdSchema,
        quantity: {
          type: 'integer',
          minimum: 1,
          maximum: 5,
          description: 'Number of units to add, from 1 to 5.',
        },
      },
      required: ['productId', 'quantity'],
    },
    annotations: {
      readOnlyHint: false,
      untrustedContentHint: false,
    },
    execute: (args, options) => handlersRef.current.addToCart(args, options),
  };
}

export function useWebMcpTools(handlers) {
  const handlersRef = useRef(handlers);

  useLayoutEffect(() => {
    handlersRef.current = handlers;
  });

  const [status, setStatus] = useState({ state: 'checking', message: 'Проверяем WebMCP' });

  useEffect(() => {
    const modelContext = document.modelContext;

    if (!modelContext?.registerTool) {
      setStatus({
        state: 'unsupported',
        message: 'Обычный режим: WebMCP недоступен',
      });
      return undefined;
    }

    const controller = new AbortController();
    const tools = [
      createSearchProductsTool(handlersRef),
      createShowProductTool(handlersRef),
      createAddToCartTool(handlersRef),
    ];

    async function registerTools() {
      try {
        await Promise.all(
          tools.map((tool) => modelContext.registerTool(tool, { signal: controller.signal })),
        );

        if (!controller.signal.aborted) {
          setStatus({ state: 'ready', message: `${tools.length} WebMCP-инструмента активны` });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          controller.abort();
          setStatus({
            state: 'error',
            message: `WebMCP не запущен: ${error.message}`,
          });
        }
      }
    }

    registerTools();

    return () => controller.abort();
  }, []);

  return status;
}

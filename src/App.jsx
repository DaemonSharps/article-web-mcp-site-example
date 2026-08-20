import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { categories, formatPrice, getCategoryLabel, products } from './products.js';
import { useWebMcpTools } from './useWebMcpTools.js';

const initialFilters = {
  query: '',
  category: 'all',
  maxPrice: '',
  inStockOnly: false,
};

function matchesFilters(product, filters) {
  const query = filters.query.trim().toLocaleLowerCase('ru-RU');
  const searchableText = [product.name, product.description, ...product.tags]
    .join(' ')
    .toLocaleLowerCase('ru-RU');
  const maxPrice = filters.maxPrice === '' ? Number.POSITIVE_INFINITY : Number(filters.maxPrice);

  return (
    (!query || searchableText.includes(query)) &&
    (filters.category === 'all' || product.category === filters.category) &&
    product.price <= maxPrice &&
    (!filters.inStockOnly || product.stock > 0)
  );
}

function ProductArt({ product, large = false }) {
  return (
    <div
      className={`product-art ${large ? 'product-art--large' : ''}`}
      style={{ '--accent': product.accent }}
      aria-hidden="true"
    >
      <span>{product.mark}</span>
      <i />
    </div>
  );
}

export default function App() {
  const [filters, setFilters] = useState(initialFilters);
  const [cart, setCart] = useState({});
  const [selectedProduct, setSelectedProduct] = useState(null);
  const [cartOpen, setCartOpen] = useState(false);
  const [notice, setNotice] = useState('');
  const cartRef = useRef(cart);
  const modalReturnFocusRef = useRef(null);

  useLayoutEffect(() => {
    cartRef.current = cart;
  }, [cart]);

  const filteredProducts = useMemo(
    () => products.filter((product) => matchesFilters(product, filters)),
    [filters],
  );

  const cartItems = products
    .filter((product) => cart[product.id])
    .map((product) => ({ ...product, quantity: cart[product.id] }));
  const cartCount = cartItems.reduce((total, item) => total + item.quantity, 0);
  const cartTotal = cartItems.reduce((total, item) => total + item.price * item.quantity, 0);
  const modalOpen = Boolean(selectedProduct) || cartOpen;

  useEffect(() => {
    if (!modalOpen) {
      return undefined;
    }

    modalReturnFocusRef.current = document.activeElement;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.body.style.overflow = previousOverflow;
      window.requestAnimationFrame(() => modalReturnFocusRef.current?.focus?.());
    };
  }, [modalOpen]);

  useEffect(() => {
    if (!modalOpen) {
      return undefined;
    }

    const dialog = document.querySelector(cartOpen ? '.cart-drawer' : '.product-dialog');
    const focusableSelector = 'button:not(:disabled), a[href], input:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex="-1"])';
    const animationFrame = window.requestAnimationFrame(() => {
      dialog?.querySelector(focusableSelector)?.focus();
    });

    function handleKeyDown(event) {
      if (event.key === 'Escape') {
        setSelectedProduct(null);
        setCartOpen(false);
        return;
      }

      if (event.key !== 'Tab' || !dialog) {
        return;
      }

      const focusable = [...dialog.querySelectorAll(focusableSelector)];
      const first = focusable[0];
      const last = focusable.at(-1);

      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    }

    document.addEventListener('keydown', handleKeyDown);

    return () => {
      window.cancelAnimationFrame(animationFrame);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [modalOpen, cartOpen, selectedProduct?.id]);

  function flash(message) {
    setNotice(message);
    window.setTimeout(() => setNotice(''), 2600);
  }

  function searchProducts(args = {}) {
    if (!args || typeof args !== 'object' || Array.isArray(args)) {
      throw new Error('Tool arguments must be an object.');
    }
    if (args.query !== undefined && typeof args.query !== 'string') {
      throw new Error('query must be a string.');
    }
    if (args.inStockOnly !== undefined && typeof args.inStockOnly !== 'boolean') {
      throw new Error('inStockOnly must be a boolean.');
    }

    const nextFilters = {
      query: args.query ?? '',
      category: args.category ?? 'all',
      maxPrice: args.maxPrice ?? '',
      inStockOnly: args.inStockOnly ?? false,
    };

    if (!categories.some((category) => category.value === nextFilters.category)) {
      throw new Error(`Unknown category: ${nextFilters.category}`);
    }
    if (nextFilters.maxPrice !== '' && (!Number.isFinite(nextFilters.maxPrice) || nextFilters.maxPrice < 0)) {
      throw new Error('maxPrice must be a non-negative number in Russian rubles.');
    }

    const matches = products.filter((product) => matchesFilters(product, nextFilters));
    flushSync(() => setFilters(nextFilters));
    document.querySelector('#catalog')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    flash(`Агент настроил каталог: найдено ${matches.length}`);

    return {
      count: matches.length,
      products: matches.map(({ id, name, category, price, stock, tags }) => ({
        id,
        name,
        category,
        price,
        stock,
        features: tags,
      })),
    };
  }

  function showProduct(args = {}) {
    if (!args || typeof args !== 'object' || Array.isArray(args)) {
      throw new Error('Tool arguments must be an object.');
    }

    const { productId } = args;
    if (typeof productId !== 'string') {
      throw new Error('productId must be a string.');
    }

    const product = products.find((item) => item.id === productId);
    if (!product) {
      throw new Error(`Product "${productId}" was not found. Call search_products to get a valid id.`);
    }

    flushSync(() => {
      setCartOpen(false);
      setSelectedProduct(product);
    });
    flash(`Открыли ${product.name}`);
    return {
      id: product.id,
      name: product.name,
      description: product.description,
      price: product.price,
      stock: product.stock,
      features: product.tags,
    };
  }

  function addToCart(args = {}) {
    if (!args || typeof args !== 'object' || Array.isArray(args)) {
      throw new Error('Tool arguments must be an object.');
    }

    const { productId, quantity } = args;
    if (typeof productId !== 'string') {
      throw new Error('productId must be a string.');
    }

    const product = products.find((item) => item.id === productId);
    if (!product) {
      throw new Error(`Product "${productId}" was not found. Call search_products to get a valid id.`);
    }
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > 5) {
      throw new Error('quantity must be an integer from 1 to 5.');
    }

    const currentQuantity = cartRef.current[productId] ?? 0;
    if (product.stock === 0 || currentQuantity + quantity > product.stock) {
      throw new Error(`Only ${product.stock} unit(s) of ${product.name} are available; ${currentQuantity} are already in the cart.`);
    }

    const nextCart = { ...cartRef.current, [productId]: currentQuantity + quantity };
    cartRef.current = nextCart;
    flushSync(() => {
      setCart(nextCart);
      setSelectedProduct(null);
      setCartOpen(true);
    });
    flash(`${product.name} добавлен в корзину`);

    return {
      success: true,
      productId,
      quantityInCart: nextCart[productId],
      message: 'The cart was updated and opened for user review. No order was placed.',
    };
  }

  const webMcpStatus = useWebMcpTools({ searchProducts, showProduct, addToCart });

  function handleFilterChange(event) {
    const { name, value, checked, type } = event.target;
    setFilters((current) => ({ ...current, [name]: type === 'checkbox' ? checked : value }));
  }

  return (
    <div className="site-shell">
      <header className="topbar" inert={modalOpen || undefined}>
        <a className="brand" href="#top" aria-label="Signal Store, на главную">
          <span className="brand__signal" aria-hidden="true"><i /><i /><i /></span>
          <span>SIGNAL<small>/STORE</small></span>
        </a>
        <nav aria-label="Основная навигация">
          <a href="#catalog">Каталог</a>
          <a href="#principles">Почему мы</a>
        </nav>
        <button className="cart-button" type="button" onClick={() => setCartOpen(true)}>
          Корзина <span>{cartCount}</span>
        </button>
      </header>

      <main id="top" inert={modalOpen || undefined}>
        <section className="hero">
          <div className="hero__copy">
            <p className="eyebrow">Техника без белого шума</p>
            <h1>Выбирайте сигнал.<br /><em>Убирайте лишнее.</em></h1>
            <p className="hero__lead">
              Небольшая витрина устройств, которые не требуют сорока вкладок со сравнениями.
              Теперь каталог понимают и люди, и браузерные агенты.
            </p>
            <div className="hero__actions">
              <a className="primary-action" href="#catalog">Смотреть устройства</a>
              <span className={`mcp-badge mcp-badge--${webMcpStatus.state}`}>
                <i /> {webMcpStatus.message}
              </span>
            </div>
          </div>
          <div className="hero__visual" aria-label="Подборка устройств Signal Store">
            <div className="hero-card hero-card--back"><ProductArt product={products[2]} /></div>
            <div className="hero-card hero-card--front"><ProductArt product={products[0]} large /></div>
            <div className="hero-sticker">6 устройств<br /><strong>0 случайных</strong></div>
          </div>
        </section>

        <section className="catalog" id="catalog">
          <div className="section-heading">
            <div>
              <p className="eyebrow">Каталог / 2026</p>
              <h2>Полезная электроника</h2>
            </div>
            <p>{filteredProducts.length} из {products.length} устройств</p>
          </div>

          <div className="filters" aria-label="Фильтры каталога">
            <label className="search-field">
              <span>Поиск</span>
              <input
                name="query"
                value={filters.query}
                onChange={handleFilterChange}
                placeholder="Например, OLED или ANC"
              />
            </label>
            <label>
              <span>Категория</span>
              <select name="category" value={filters.category} onChange={handleFilterChange}>
                {categories.map((category) => (
                  <option value={category.value} key={category.value}>{category.label}</option>
                ))}
              </select>
            </label>
            <label>
              <span>Цена до, ₽</span>
              <input
                name="maxPrice"
                type="number"
                min="0"
                value={filters.maxPrice}
                onChange={handleFilterChange}
                placeholder="Без лимита"
              />
            </label>
            <label className="check-field">
              <input
                name="inStockOnly"
                type="checkbox"
                checked={filters.inStockOnly}
                onChange={handleFilterChange}
              />
              <span>Только в наличии</span>
            </label>
            <button className="reset-button" type="button" onClick={() => setFilters(initialFilters)}>
              Сбросить
            </button>
          </div>

          <div className="product-grid" aria-live="polite">
            {filteredProducts.map((product) => (
              <article className="product-card" key={product.id}>
                <button
                  className="product-card__open"
                  type="button"
                  onClick={() => showProduct({ productId: product.id })}
                  aria-label={`Открыть ${product.name}`}
                >
                  <ProductArt product={product} />
                </button>
                <div className="product-card__meta">
                  <span>{getCategoryLabel(product.category)}</span>
                  <span className={product.stock ? '' : 'sold-out'}>
                    {product.stock ? `В наличии: ${product.stock}` : 'Нет в наличии'}
                  </span>
                </div>
                <h3>{product.name}</h3>
                <p>{product.description}</p>
                <div className="product-card__footer">
                  <strong>{formatPrice(product.price)}</strong>
                  <button
                    type="button"
                    disabled={!product.stock}
                    onClick={() => addToCart({ productId: product.id, quantity: 1 })}
                  >
                    {product.stock ? 'В корзину' : 'Ждём поставку'}
                  </button>
                </div>
              </article>
            ))}
          </div>

          {!filteredProducts.length && (
            <div className="empty-state">
              <strong>Сигнал потерян</strong>
              <p>Под такие параметры ничего не нашлось. Ослабьте фильтры.</p>
            </div>
          )}
        </section>

        <section className="principles" id="principles">
          <p className="eyebrow">Наш короткий манифест</p>
          <div className="principles__grid">
            <h2>Меньше выбора.<br />Больше ясности.</h2>
            <ol>
              <li><span>01</span>Показываем понятные характеристики, а не облако маркетинга.</li>
              <li><span>02</span>Не прячем наличие и финальную цену за семью кликами.</li>
              <li><span>03</span>Любое действие агента сразу видно в том же интерфейсе.</li>
            </ol>
          </div>
        </section>
      </main>

      <footer inert={modalOpen || undefined}>
        <span>SIGNAL / STORE</span>
        <span>Демонстрационный магазин, 2026</span>
      </footer>

      {selectedProduct && (
        <div className="overlay" role="presentation" onMouseDown={() => setSelectedProduct(null)}>
          <section
            className="product-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="product-dialog-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <button className="close-button" type="button" onClick={() => setSelectedProduct(null)} aria-label="Закрыть">×</button>
            <ProductArt product={selectedProduct} large />
            <p className="eyebrow">{getCategoryLabel(selectedProduct.category)}</p>
            <h2 id="product-dialog-title">{selectedProduct.name}</h2>
            <p>{selectedProduct.description}</p>
            <ul>{selectedProduct.tags.map((tag) => <li key={tag}>{tag}</li>)}</ul>
            <div className="dialog-footer">
              <strong>{formatPrice(selectedProduct.price)}</strong>
              <button
                className="primary-action"
                type="button"
                disabled={!selectedProduct.stock}
                onClick={() => addToCart({ productId: selectedProduct.id, quantity: 1 })}
              >
                {selectedProduct.stock ? 'Добавить в корзину' : 'Нет в наличии'}
              </button>
            </div>
          </section>
        </div>
      )}

      {cartOpen && (
        <div className="overlay overlay--cart" role="presentation" onMouseDown={() => setCartOpen(false)}>
          <aside
            className="cart-drawer"
            role="dialog"
            aria-modal="true"
            aria-labelledby="cart-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <button className="close-button" type="button" onClick={() => setCartOpen(false)} aria-label="Закрыть корзину">×</button>
            <p className="eyebrow">Проверьте перед заказом</p>
            <h2 id="cart-title">Корзина <sup>{cartCount}</sup></h2>
            {cartItems.length ? (
              <>
                <div className="cart-list">
                  {cartItems.map((item) => (
                    <div className="cart-item" key={item.id}>
                      <ProductArt product={item} />
                      <div><strong>{item.name}</strong><span>{item.quantity} × {formatPrice(item.price)}</span></div>
                      <button
                        type="button"
                        onClick={() => {
                          const nextCart = { ...cartRef.current };
                          delete nextCart[item.id];
                          cartRef.current = nextCart;
                          setCart(nextCart);
                        }}
                        aria-label={`Удалить ${item.name}`}
                      >×</button>
                    </div>
                  ))}
                </div>
                <div className="cart-total"><span>Итого</span><strong>{formatPrice(cartTotal)}</strong></div>
                <button className="primary-action checkout-button" type="button" onClick={() => flash('Это демо: деньги спасены')}>
                  Перейти к оформлению
                </button>
              </>
            ) : <p className="cart-empty">Здесь пока тихо. Добавьте что-нибудь из каталога.</p>}
          </aside>
        </div>
      )}

      {notice && <div className="toast" role="status">{notice}</div>}
    </div>
  );
}

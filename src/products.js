export const categories = [
  { value: 'all', label: 'Все' },
  { value: 'audio', label: 'Аудио' },
  { value: 'computers', label: 'Компьютеры' },
  { value: 'wearables', label: 'Гаджеты' },
  { value: 'workspace', label: 'Рабочее место' },
];

export const products = [
  {
    id: 'orbit-buds',
    name: 'Orbit Buds S',
    category: 'audio',
    price: 6990,
    stock: 14,
    accent: '#ff6b35',
    mark: 'OB',
    description: 'Компактные TWS-наушники с шумоподавлением и 28 часами работы.',
    tags: ['Bluetooth 5.4', 'ANC', '28 часов'],
  },
  {
    id: 'vector-air',
    name: 'VectorBook Air 14',
    category: 'computers',
    price: 89990,
    stock: 3,
    accent: '#a3e635',
    mark: 'VA',
    description: 'Лёгкий ноутбук с OLED-экраном, 16 Гб памяти и тихим характером.',
    tags: ['OLED 2.8K', '16 / 512 Гб', '1.18 кг'],
  },
  {
    id: 'pulse-watch',
    name: 'Pulse Watch 2',
    category: 'wearables',
    price: 14990,
    stock: 8,
    accent: '#38bdf8',
    mark: 'PW',
    description: 'Часы для спорта, сна и убедительных напоминаний встать со стула.',
    tags: ['GPS', '5 ATM', '10 дней'],
  },
  {
    id: 'frame-monitor',
    name: 'Frame 27 Studio',
    category: 'workspace',
    price: 32990,
    stock: 0,
    accent: '#c084fc',
    mark: 'F27',
    description: '27-дюймовый 4K-монитор с USB-C и заводской калибровкой цвета.',
    tags: ['4K IPS', 'USB-C 90 Вт', 'sRGB 100%'],
  },
  {
    id: 'key-one',
    name: 'Key One Low',
    category: 'workspace',
    price: 8490,
    stock: 11,
    accent: '#facc15',
    mark: 'K1',
    description: 'Низкопрофильная механическая клавиатура без офисного пулемёта.',
    tags: ['Hot-swap', '2.4 ГГц', '75%'],
  },
  {
    id: 'beam-speaker',
    name: 'Beam Mini',
    category: 'audio',
    price: 11990,
    stock: 5,
    accent: '#fb7185',
    mark: 'BM',
    description: 'Домашняя колонка с насыщенным звуком и мультирум-синхронизацией.',
    tags: ['Wi-Fi', 'AirPlay', '40 Вт'],
  },
];

export function getCategoryLabel(value) {
  return categories.find((category) => category.value === value)?.label ?? value;
}

export function formatPrice(value) {
  return new Intl.NumberFormat('ru-RU').format(value) + ' ₽';
}

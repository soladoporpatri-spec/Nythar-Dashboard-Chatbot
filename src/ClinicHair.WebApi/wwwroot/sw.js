const CACHE_NAME = 'clinichair-pwa-v7';
const APP_SHELL = [
  '/',
  '/index.html',
  '/css/dashboard.css',
  '/js/icons.js',
  '/js/dashboard.js',
  '/js/ext-ux.js',
  '/js/ext-panel.js',
  '/css/ext.css',
  '/manifest.webmanifest',
  '/icons/app-192.png',
  '/icons/app-512.png'
];

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL)).catch(() => {})
  );
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys =>
      Promise.all(
        keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key))
      )
    )
  );
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  const { request } = event;
  if (request.method !== 'GET') return;

  const url = new URL(request.url);
  if (url.pathname.startsWith('/api/')) {
    return;
  }

  event.respondWith(
    fetch(request)
      .then(response => {
        if (response && response.status === 200 && response.type === 'basic') {
          const cloned = response.clone();
          caches.open(CACHE_NAME).then(cache => cache.put(request, cloned)).catch(() => {});
        }
        return response;
      })
      .catch(() => caches.match(request).then(cached => cached || caches.match('/index.html')))
  );
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  const targetSection = event.notification.data?.topic || 'agenda';

  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windowClients => {
      for (const client of windowClients) {
        if ('focus' in client) {
          client.postMessage({ type: 'NOTIFICATION_CLICK', topic: targetSection });
          return client.focus();
        }
      }
      if (self.clients.openWindow) {
        return self.clients.openWindow('/');
      }
    })
  );
});

// Minimal service worker: cache the app shell so the PWA opens instantly / offline.
// Live data always comes over the WebSocket, never cached.
var CACHE = 'orderflow-v14';
var SHELL = ['/', '/index.html', '/app.js', '/footprint-primitive.js', '/vp-primitive.js', '/table-primitive.js', '/draw-primitive.js', '/manifest.webmanifest', '/icon.svg'];

self.addEventListener('install', function (e) {
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.addAll(SHELL); }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (e) {
  e.waitUntil(
    caches.keys().then(function (keys) {
      return Promise.all(keys.filter(function (k) { return k !== CACHE; }).map(function (k) { return caches.delete(k); }));
    }).then(function () { return self.clients.claim(); })
  );
});

self.addEventListener('fetch', function (e) {
  var url = new URL(e.request.url);
  // Never intercept the live stream or relay endpoints.
  if (url.pathname === '/stream' || url.pathname === '/ingest' || url.pathname === '/healthz') return;
  // Cache-first for the shell, network fallback (and refresh cache) otherwise.
  e.respondWith(
    caches.match(e.request).then(function (hit) {
      return hit || fetch(e.request).then(function (resp) {
        return resp;
      }).catch(function () { return hit; });
    })
  );
});

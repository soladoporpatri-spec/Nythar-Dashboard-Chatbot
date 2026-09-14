const express = require('express');
const cors = require('cors');
const path = require('path');
const fs = require('fs');
const crypto = require('crypto');
const rateLimit = require('express-rate-limit');
const helmet = require('helmet');
const net = require('net');
const http = require('http');
const https = require('https');
const { URL } = require('url');
const { AsyncLocalStorage } = require('async_hooks');

// Propaga storeId do usuÃ¡rio autenticado para backendFetch via AsyncLocalStorage
// Sem isso, backendFetch envia apenas X-API-KEY e TenantService retorna fallback 1
const requestContext = new AsyncLocalStorage();

const app = express();
const PORT = process.env.PORT ? Number(process.env.PORT) : 4000;
const HOST = process.env.DASHBOARD_HOST || process.env.HOST || (process.env.NODE_ENV === 'production' ? '127.0.0.1' : '0.0.0.0');
const startTime = Date.now();

const BACKEND_API_KEY = process.env.API_KEY;

if (!BACKEND_API_KEY) {
    console.error('[ERRO] API_KEY nao definida. Execute node scripts/gerar-segredos.js ou inicie pelo supervisor.');
    process.exit(1);
}

const BACKEND_URL    = process.env.BACKEND_URL    || 'http://127.0.0.1:5000';
const FACTORY_URL    = `http://127.0.0.1:${process.env.FACTORY_PORT || 2999}`;
const BRIDGE_BASE_PORT = Number(process.env.BRIDGE_BASE_PORT || 3000);
const DEFAULT_BRIDGE_URL = process.env.BRIDGE_URL || 'http://127.0.0.1:3000';
const DATA_DIR = path.join(__dirname, 'data');
const STORES_META_FILE = path.join(DATA_DIR, 'stores-meta.json');
const LAVAJATO_DIR = path.join(__dirname, '..', 'Lavajato');
const NYTHAR_DIR = path.join(__dirname, 'Nythar');
const SESSION_FILE = path.join(DATA_DIR, 'dashboard-sessions.json');
const SESSION_TTL_MS = Number(process.env.DASHBOARD_SESSION_TTL_MS || 7 * 24 * 60 * 60 * 1000);
const allowedOrigins = (process.env.CORS_ORIGINS || 'http://localhost,http://127.0.0.1,http://localhost:4000,http://127.0.0.1:4000')
    .split(',')
    .map(origin => origin.trim())
    .filter(Boolean);
const runtimeLocalOrigins = [
    `http://localhost:${PORT}`,
    `http://127.0.0.1:${PORT}`
];

app.set('trust proxy', 1);
app.use('/hubs/notifications', (req, res) => {
    proxySignalRRequest(req, res);
});
app.use(helmet({
    contentSecurityPolicy: {
        useDefaults: true,
        directives: {
            "default-src": ["'self'"],
            "script-src": ["'self'", "'unsafe-inline'", "https://cdn.tailwindcss.com", "https://cdn.jsdelivr.net"],
            "script-src-attr": ["'unsafe-inline'"],
            "style-src": ["'self'", "'unsafe-inline'", "https://cdnjs.cloudflare.com", "https://fonts.googleapis.com"],
            "style-src-attr": ["'unsafe-inline'"],
            "img-src": ["'self'", "data:", "https://images.unsplash.com"],
            "font-src": ["'self'", "https://cdnjs.cloudflare.com", "https://fonts.gstatic.com", "data:"],
            "frame-src": ["'self'", "https://www.google.com"],
            "connect-src": ["'self'", "*"] // Permite conexÃµes para qualquer origem (necessÃ¡rio para Ngrok dinÃ¢mico)
        }
    }
}));
app.disable('x-powered-by');
app.use(express.json({ limit: '10mb' }));
app.use(cors({
    origin(origin, callback) {
        // Permite requisiÃ§Ãµes locais, do domÃ­nio configurado ou ngrok (sempre â€” sistema local usa ngrok)
        if (!origin || origin === 'null' || allowedOrigins.includes(origin) || runtimeLocalOrigins.includes(origin) || origin.endsWith('.ngrok-free.dev') || origin.endsWith('.ngrok-free.app')) {
            return callback(null, true);
        }
        console.warn(`[CORS] Bloqueado: ${origin}`);
        callback(new Error('Origem nao autorizada pelo CORS'));
    },
    credentials: true
}));

// Rate limiting global
const limiter = rateLimit({
    windowMs: 15 * 60 * 1000, // 15 minutes
    max: 2000, // dashboard faz polling frequente; manter margem para uso local normal
    standardHeaders: true,
    legacyHeaders: false,
    skip: req => req.path === '/health' || req.path === '/health/deep',
    message: 'Muitas requisiÃ§Ãµes deste IP, tente novamente mais tarde.'
});
app.use(limiter);

const publicFiles = new Set([
    'dashboard-improved.html',
    'login.html',
    'script-dashboard.js',
    'style-dashboard.css',
    'manifest.json',
    'sw.js',
    'test.html'
]);
app.use('/assets', express.static(path.join(__dirname, 'assets'), { index: false, dotfiles: 'deny' }));
app.use('/superadmin', express.static(path.join(__dirname, 'superadmin'), { index: 'index.html', dotfiles: 'deny' }));
app.use('/lavajato', express.static(LAVAJATO_DIR, { index: 'index.html', dotfiles: 'deny' }));
app.use('/nythar', express.static(NYTHAR_DIR, { index: 'landing-page.html', dotfiles: 'deny' }));
app.get('/landing', (_req, res) => res.redirect('/nythar/'));
app.get('/', (req, res) => res.redirect('/dashboard-improved.html'));
app.get('/:file', (req, res, next) => {
    if (!publicFiles.has(req.params.file)) return next();
    res.sendFile(path.join(__dirname, req.params.file));
});

// Credenciais NUNCA vÃ£o para o frontend
const TEMPLATES_PATH = path.join(__dirname, 'data', 'whatsapp-templates.json');

// SessÃµes simples em memÃ³ria (token â†’ expiry)
const sessions = new Map();

function ensureDataDir() {
    if (!fs.existsSync(DATA_DIR)) fs.mkdirSync(DATA_DIR, { recursive: true });
}

function loadSessionsFromDisk() {
    ensureDataDir();
    if (!fs.existsSync(SESSION_FILE)) return;
    try {
        const raw = JSON.parse(fs.readFileSync(SESSION_FILE, 'utf8'));
        const items = Array.isArray(raw) ? raw : Object.entries(raw || {}).map(([token, session]) => ({ token, ...session }));
        const now = Date.now();
        for (const item of items) {
            if (!item?.token || !item.expiry || item.expiry <= now) continue;
            sessions.set(item.token, {
                expiry: item.expiry,
                role: item.role,
                barberId: item.barberId,
                storeId: item.storeId,
                storeName: item.storeName,
                bridgeUrl: item.bridgeUrl
            });
        }
        console.log(`[Dashboard] Sessoes restauradas: ${sessions.size}`);
    } catch (error) {
        console.warn('[Dashboard] Nao foi possivel restaurar sessoes:', error.message);
    }
}

function saveSessionsToDisk() {
    ensureDataDir();
    const now = Date.now();
    const items = [];
    for (const [token, session] of sessions.entries()) {
        if (!session?.expiry || session.expiry <= now) {
            sessions.delete(token);
            continue;
        }
        items.push({ token, ...session });
    }
    try {
        fs.writeFileSync(SESSION_FILE, JSON.stringify(items, null, 2), 'utf8');
    } catch (error) {
        console.warn('[Dashboard] Nao foi possivel persistir sessoes:', error.message);
    }
}

loadSessionsFromDisk();
const sessionSaveTimer = setInterval(saveSessionsToDisk, 60 * 1000);

function loadStoresMeta() {
    try {
        if (fs.existsSync(STORES_META_FILE)) {
            return JSON.parse(fs.readFileSync(STORES_META_FILE, 'utf8'));
        }
    } catch (e) {
        console.warn('[StoresMeta] Falha ao carregar stores-meta.json:', e.message);
    }
    return {};
}

async function backendFetchForStore(storeId, apiPath, options = {}) {
    return new Promise((resolve, reject) => {
        requestContext.run({ storeId }, async () => {
            try { resolve(await backendFetch(apiPath, options)); }
            catch (e) { reject(e); }
        });
    });
}

function getStoreValue(store, ...keys) {
    for (const key of keys) {
        if (store && store[key] !== undefined && store[key] !== null && store[key] !== '') return store[key];
    }
    return undefined;
}

function bridgePortForStoreId(storeId) {
    const id = Number(storeId);
    return Number.isFinite(id) && id > 0 ? BRIDGE_BASE_PORT + id : null;
}

function expectedBridgeUrlForStoreId(storeId) {
    const port = bridgePortForStoreId(storeId);
    return port ? `http://127.0.0.1:${port}` : '';
}

function enrichStorePayload(store = {}) {
    const id = Number(getStoreValue(store, 'Id', 'id', 'StoreId', 'storeId'));
    const bridgePort = Number(getStoreValue(store, 'BridgePort', 'bridgePort')) || bridgePortForStoreId(id);
    const bridgeUrl = getStoreValue(store, 'BridgeUrl', 'bridgeUrl') || expectedBridgeUrlForStoreId(id);
    const backendUrl = getStoreValue(store, 'BackendUrl', 'backendUrl') || BACKEND_URL;
    const provisioning = getStoreValue(store, 'Provisioning', 'provisioning');
    const normalizedProvisioning = provisioning ? {
        storeId: Number(getStoreValue(provisioning, 'storeId', 'StoreId')) || id,
        bridgeBasePort: Number(getStoreValue(provisioning, 'bridgeBasePort', 'BridgeBasePort')) || BRIDGE_BASE_PORT,
        bridgePort: Number(getStoreValue(provisioning, 'bridgePort', 'BridgePort')) || bridgePort,
        bridgeUrl: getStoreValue(provisioning, 'bridgeUrl', 'BridgeUrl') || bridgeUrl,
        backendUrl: getStoreValue(provisioning, 'backendUrl', 'BackendUrl') || backendUrl,
        portRule: getStoreValue(provisioning, 'portRule', 'PortRule') || 'bridgePort = 3000 + storeId'
    } : null;

    return {
        id,
        storeId: id,
        name: getStoreValue(store, 'Name', 'name'),
        slug: getStoreValue(store, 'Slug', 'slug'),
        plan: getStoreValue(store, 'Plan', 'plan'),
        createdAt: getStoreValue(store, 'CreatedAt', 'createdAt'),
        expiresAt: getStoreValue(store, 'ExpiresAt', 'expiresAt'),
        subscriptionExpiry: getStoreValue(store, 'SubscriptionExpiry', 'subscriptionExpiry'),
        isActive: getStoreValue(store, 'IsActive', 'isActive'),
        isSuspended: getStoreValue(store, 'IsSuspended', 'isSuspended'),
        commercialStatus: getStoreValue(store, 'CommercialStatus', 'commercialStatus'),
        businessType: getStoreValue(store, 'BusinessType', 'businessType'),
        apiKey: getStoreValue(store, 'ApiKey', 'apiKey'),
        backendUrl,
        bridgeUrl,
        bridgePort,
        expectedBridgeUrl: expectedBridgeUrlForStoreId(id),
        provisioning: normalizedProvisioning
    };
}

function toStoreMeta(store, previous = {}, services = []) {
    const enriched = enrichStorePayload(store);
    const storeId = Number(enriched.storeId);
    const slug = String(getStoreValue(enriched, 'Slug', 'slug') || previous.slug || '').trim().toLowerCase();
    return {
        ...previous,
        storeId,
        name: getStoreValue(enriched, 'Name', 'name') || previous.name || slug,
        businessType: getStoreValue(enriched, 'BusinessType', 'businessType') || previous.businessType || 'Barbershop',
        phone: previous.phone || '',
        bridgePort: enriched.bridgePort,
        bridgeUrl: enriched.bridgeUrl,
        backendUrl: enriched.backendUrl,
        plan: getStoreValue(enriched, 'Plan', 'plan') || previous.plan || 'Professional',
        slug,
        services: services.length ? services : previous.services || []
    };
}

async function syncStoresMetaFromBackend(reason = 'manual') {
    const stores = await backendFetch('/api/superadmin/stores');
    const current = loadStoresMeta();
    const next = {};

    for (const rawStore of Array.isArray(stores) ? stores : []) {
        const store = enrichStorePayload(rawStore);
        if (getStoreValue(store, 'IsActive', 'isActive') === false) continue;
        const storeId = Number(store.storeId);
        const slug = String(getStoreValue(store, 'Slug', 'slug') || '').trim().toLowerCase();
        if (!storeId || !slug) continue;

        let services = [];
        try {
            const svc = await backendFetchForStore(storeId, '/api/servicos');
            services = (Array.isArray(svc) ? svc : [])
                .filter(item => item.Ativo !== false && item.ativo !== false)
                .map(item => ({
                    id: String(getStoreValue(item, 'Id', 'id')),
                    name: getStoreValue(item, 'Nome', 'nome', 'Name', 'name') || '',
                    price: Number(getStoreValue(item, 'Preco', 'preco', 'Price', 'price') || 0),
                    duration: Number(getStoreValue(item, 'DuracaoMinutos', 'duracaoMinutos', 'DurationMinutes', 'durationMinutes') || 0)
                }))
                .filter(item => item.id && item.name);
        } catch (e) {
            console.warn(`[StoresMeta] Nao foi possivel carregar servicos da loja ${storeId}: ${e.message}`);
        }

        next[slug] = toStoreMeta(store, current[slug] || {}, services);
    }

    ensureDataDir();
    fs.writeFileSync(STORES_META_FILE, JSON.stringify(next, null, 2), 'utf8');
    console.log(`[StoresMeta] Sincronizado (${reason}): ${Object.keys(next).length} loja(s).`);
    return { ok: true, count: Object.keys(next).length, stores: next };
}
if (sessionSaveTimer.unref) sessionSaveTimer.unref();

function withTimeout(ms) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), ms);
    return { signal: controller.signal, done: () => clearTimeout(timeout) };
}

function proxySignalRRequest(req, res) {
    const target = new URL(BACKEND_URL);
    const transport = target.protocol === 'https:' ? https : http;
    const headers = {
        ...req.headers,
        host: target.host,
        'x-forwarded-proto': req.protocol,
        'x-forwarded-host': req.headers.host || ''
    };
    delete headers['content-length'];

    const upstream = transport.request({
        protocol: target.protocol,
        hostname: target.hostname,
        port: target.port || (target.protocol === 'https:' ? 443 : 80),
        method: req.method,
        path: req.originalUrl,
        headers
    }, upstreamRes => {
        res.writeHead(upstreamRes.statusCode || 502, upstreamRes.headers);
        upstreamRes.pipe(res);
    });

    upstream.setTimeout(Number(process.env.SIGNALR_PROXY_TIMEOUT_MS || 70000), () => {
        upstream.destroy(new Error('Timeout no proxy SignalR'));
    });

    upstream.on('error', error => {
        console.error('[SignalR Proxy] Falha ao encaminhar request:', error.message);
        if (!res.headersSent) {
            res.status(502).json({ error: 'SignalR backend indisponivel' });
        } else {
            res.destroy(error);
        }
    });

    if (['GET', 'HEAD'].includes(req.method)) {
        upstream.end();
        return;
    }

    const body = req.body && Object.keys(req.body).length ? JSON.stringify(req.body) : '';
    if (body) {
        upstream.setHeader('content-type', req.headers['content-type'] || 'application/json');
        upstream.setHeader('content-length', Buffer.byteLength(body));
        upstream.end(body);
    } else {
        req.pipe(upstream);
    }
}

function gerarToken() {
    return crypto.randomBytes(32).toString('base64url');
}

// Rate limit simples
const rateLimits = new Map();
function rateLimitLocal(req, res, next) {
    const ip = req.ip;
    const agora = Date.now();
    const janela = rateLimits.get(ip) || [];
    const recentes = janela.filter(t => agora - t < 60000); // 1 min
    if (recentes.length >= 60) {
        return res.status(429).json({ error: 'Muitas requisiÃ§Ãµes. Aguarde 1 minuto.' });
    }
    recentes.push(agora);
    rateLimits.set(ip, recentes);
    next();
}

// Middleware de autenticaÃ§Ã£o por sessÃ£o
function auth(req, res, next) {
    let token = req.headers['x-session-token'];

    // Suporte ao padrÃ£o Bearer enviado pelo script-dashboard.js
    if (!token && req.headers['authorization']) {
        const parts = req.headers['authorization'].split(' ');
        if (parts.length === 2 && parts[0] === 'Bearer') token = parts[1];
    }

    if (!token) return res.status(401).json({ error: 'Sem sessao' });
    const session = sessions.get(token);
    if (!session || Date.now() > session.expiry) {
        sessions.delete(token);
        return res.status(401).json({ error: 'Sessao expirada' });
    }
    // Renova sessÃ£o a cada request
    session.expiry = Date.now() + SESSION_TTL_MS;
    if (!session.lastSavedAt || Date.now() - session.lastSavedAt > 60 * 1000) {
        session.lastSavedAt = Date.now();
        saveSessionsToDisk();
    }
    req.user = session;
    // Propaga storeId pelo requestContext para que backendFetch adicione X-Store-Id
    requestContext.run({ storeId: session.storeId || null }, next);
}

// Proxy para o backend C#
async function backendFetch(path, options = {}) {
    const nodeFetch = (await import('node-fetch')).default;
    const timeout = withTimeout(15000);
    try {
        const url = `${BACKEND_URL}${path}`;
        // LÃª storeId do contexto assÃ­ncrono (setado pelo middleware auth)
        // Isso garante que cada requisiÃ§Ã£o carregue o tenant correto para o backend
        const ctx = requestContext.getStore();
        const storeId = ctx?.storeId;
        const storeHeader = (storeId !== null && storeId !== undefined)
            ? { 'X-Store-Id': String(storeId) }
            : {};
        const res = await nodeFetch(url, {
            ...options,
            signal: timeout.signal,
            headers: {
                'X-API-KEY': BACKEND_API_KEY.trim(),
                'Content-Type': 'application/json',
                ...storeHeader,
                ...options.headers
            }
        });

        const contentType = res.headers.get('content-type');
        const data = (contentType && contentType.includes('application/json')) ? await res.json() : {};

        if (!res.ok) {
            const error = new Error(data.error || `Backend retornou ${res.status}`);
            error.status = res.status;
            error.data = data;
            error.backendPath = path;
            throw error;
        }
        return data;
    } catch (e) {
        if (e.name === 'AbortError') {
            const error = new Error(`Timeout ao chamar backend em ${path}`);
            error.status = 504;
            error.backendPath = path;
            throw error;
        }
        if (!e.backendPath) e.backendPath = path;
        throw e;
    } finally {
        timeout.done();
    }
}

async function bridgeFetch(req, path, options = {}) {
    // Pega a URL da Bridge configurada para ESTA loja especÃ­fica no login
    const bridgeUrl = req.user?.bridgeUrl || DEFAULT_BRIDGE_URL;
    
    const fetch = (await import('node-fetch')).default;
    const timeout = withTimeout(15000); // Aumentado para dar mais tempo ao Bridge
    let res;

    try {
        res = await fetch(`${bridgeUrl}${path}`, {
            ...options,
            signal: timeout.signal,
            headers: {
                'X-API-KEY': BACKEND_API_KEY.trim(),
                'Content-Type': 'application/json',
                ...options.headers
            }
        });

        let data = {};
        try {
            const text = await res.text();
            data = text ? JSON.parse(text) : {};
        } catch (parseError) {
            console.error('Erro ao processar JSON do Bridge:', parseError.message);
        }

        if (!res.ok) {
            throw new Error(data.error || `Bridge retornou ${res.status}`);
        }

        return data;
    } finally {
        timeout.done();
    }
}

async function ensureBarberOwnsAppointment(req, appointmentId) {
    if (req.user.role !== 'barbeiro' || !req.user.barberId) return true;

    const barberId = String(req.user.barberId);
    const id = String(appointmentId);
    const [today, future] = await Promise.all([
        backendFetch(`/api/hoje?barberId=${encodeURIComponent(barberId)}`).catch(() => []),
        backendFetch(`/api/agendamentos?barberId=${encodeURIComponent(barberId)}&pageSize=500`).catch(() => ({ data: [] }))
    ]);

    const items = [
        ...(Array.isArray(today) ? today : []),
        ...(Array.isArray(future?.data) ? future.data : Array.isArray(future) ? future : [])
    ];

    return items.some(item => String(item.Id ?? item.id) === id);
}

const DEFAULT_MESSAGE_TEMPLATES = {
    welcome: 'Ola, {nome}! Bem-vindo a {loja}.\n\nEscolha uma opcao:\n1 - Agendar horario\n2 - Meus agendamentos\n3 - Cancelar agendamento',
    confirmation: 'Agendamento confirmado! ✅\n\n📅 Data: {data}\n✂️ Serviço: {servico}\n💈 Profissional: {profissional}\n⏰ Horário: {hora}',
    reminder: 'Ola, {nome}. Passando para lembrar do seu horario em {data} para {servico}.',
    cancellation: 'Seu agendamento de {data} foi cancelado.\n\nDigite oi para marcar um novo horario.'
};

// ============ ROTAS PÃšBLICAS ============

app.get('/favicon.ico', (_req, res) => {
    res.status(204).end();
});

// Redirecionar a raiz para o dashboard
app.get('/', (req, res) => {
    res.redirect('/login.html');
});

// Login â€” recebe user/pass, devolve token de sessÃ£o
app.post('/api/auth/login', rateLimitLocal, async (req, res) => {
    const { Username, Password } = req.body;
    
    try {
        const data = await backendFetch('/api/auth/login', {
            method: 'POST',
            body: JSON.stringify({ Username, Password })
        });

        const token = gerarToken();
        sessions.set(token, {
            expiry: Date.now() + SESSION_TTL_MS,
            role: data.role,
            barberId: data.barberId,
            storeId: data.storeId,
            storeName: data.storeName,
            businessType: data.businessType,
            bridgeUrl: data.bridgeUrl // O backend .NET deve retornar isso no login
        });
        saveSessionsToDisk();
        console.log(`Login: ${Username} (${data.role}) de ${req.ip}`);
        res.json({ token, role: data.role, barberId: data.barberId, storeId: data.storeId, storeName: data.storeName, businessType: data.businessType, bridgeUrl: data.bridgeUrl, expiresIn: `${Math.round(SESSION_TTL_MS / 3600000)}h` });
    } catch (e) {
        const status = e.status || 401;
        // Preserva a mensagem real do backend (ex: loja desativada, 2FA necessÃ¡rio)
        // SÃ³ usa o genÃ©rico se nÃ£o houver mensagem especÃ­fica E for erro 401 puro
        const message = e.data?.error
            || (status === 401 ? 'Usuario ou senha incorretos' : `Erro ${status} ao autenticar`);
        console.warn(`[login] falha para "${req.body?.Username}": HTTP ${status} â€” ${message}`);
        res.status(status).json({ error: message });
    }
});

// RecuperaÃ§Ã£o de senha - Encaminha para o C#
app.post('/api/auth/recover', rateLimitLocal, async (req, res) => {
    try {
        const data = await backendFetch('/api/auth/recover', {
            method: 'POST',
            body: JSON.stringify(req.body)
        });
        res.json(data);
    } catch (e) {
        res.status(500).json({ error: 'Erro ao processar recuperaÃ§Ã£o' });
    }
});

function extractSessionToken(req) {
    let token = req.headers['x-session-token'];
    if (!token && req.headers['authorization']) {
        const parts = req.headers['authorization'].split(' ');
        if (parts.length === 2 && parts[0] === 'Bearer') token = parts[1];
    }
    return token;
}

// Logout
app.post('/api/auth/logout', auth, (req, res) => {
    const token = extractSessionToken(req);
    if (token) sessions.delete(token);
    saveSessionsToDisk();
    res.json({ ok: true });
});

// Health publico
app.get('/health', (req, res) => res.json({
    ok: true,
    service: 'dashboard-api',
    instanceId: process.env.RUNTIME_INSTANCE_ID || null,
    uptimeSeconds: Math.floor((Date.now() - startTime) / 1000),
    uptime: Math.round((Date.now() - startTime) / 60000) + 'min',
    timestamp: new Date().toISOString()
}));

function getSessionBridgeUrl(req) {
    const token = extractSessionToken(req);
    const session = token ? sessions.get(token) : undefined;
    return session?.bridgeUrl || DEFAULT_BRIDGE_URL;
}

app.get('/health/deep', async (req, res) => {
    const nodeFetch = (await import('node-fetch')).default;
    const bridgeUrl = getSessionBridgeUrl(req);
    const checks = await Promise.allSettled([
        nodeFetch(`${BACKEND_URL}/health`).then(r => r.ok),
        nodeFetch(`${bridgeUrl}/status`, {
            headers: { 'X-API-KEY': BACKEND_API_KEY.trim() }
        }).then(async r => {
            if (!r.ok) return { reachable: false, statusCode: r.status };
            const data = await r.json().catch(() => ({}));
            return {
                reachable: true,
                whatsappConnected: Boolean(data.whatsappConnected),
                botEnabled: Boolean(data.botEnabled),
                state: data.connectionState || data.state || data.status || 'unknown',
                hasQr: Boolean(data.hasQr)
            };
        }).catch(() => ({ reachable: false }))
    ]);

    const backendOk = checks[0].status === 'fulfilled' && checks[0].value;
    const bridge = checks[1].status === 'fulfilled' ? checks[1].value : { reachable: false };
    const available = backendOk && bridge.reachable;
    const ready = available && bridge.whatsappConnected;

    res.status(available ? 200 : 503).json({
        ok: available,
        ready,
        service: 'dashboard-api',
        dependencies: {
            backend: backendOk,
            bridge: Boolean(bridge.reachable),
            whatsappConnected: Boolean(bridge.whatsappConnected)
        },
        bridge,
        bridgeUrl,
        timestamp: new Date().toISOString()
    });
});

app.get('/health/local', async (req, res) => {
    const runtimePath = path.join(__dirname, 'data', 'local-runtime.json');
    let runtime = null;
    try {
        runtime = JSON.parse(fs.readFileSync(runtimePath, 'utf8'));
    } catch {}

    res.json({
        ok: true,
        service: 'dashboard-api',
        backendUrl: BACKEND_URL,
        bridgeUrl: DEFAULT_BRIDGE_URL,
        pwa: {
            manifest: fs.existsSync(path.join(__dirname, 'manifest.json')),
            serviceWorker: fs.existsSync(path.join(__dirname, 'sw.js')),
            icon192: fs.existsSync(path.join(__dirname, 'assets', 'pwa-192.png')),
            icon512: fs.existsSync(path.join(__dirname, 'assets', 'pwa-512.png')),
            https: Boolean(runtime?.publicDashboard?.startsWith('https://'))
        },
        runtime,
        timestamp: new Date().toISOString()
    });
});

// Alerta de sistema (pÃºblico â€” lido pelo dashboard sem autenticaÃ§Ã£o)
// Escrito pelo supervisor.js em data/system-alert.json quando um serviÃ§o cai 3x seguidas.
app.get('/api/system-alert', (req, res) => {
    const alertFile = path.join(DATA_DIR, 'system-alert.json');
    try {
        if (!fs.existsSync(alertFile)) return res.json({ active: false });
        const alert = JSON.parse(fs.readFileSync(alertFile, 'utf8'));
        res.json({ active: true, ...alert });
    } catch {
        res.json({ active: false });
    }
});

// ============ ROTAS PROTEGIDAS ============

// Hoje
app.get('/api/hoje', auth, async (req, res) => {
    try {
        const query = new URLSearchParams(req.query);
        if (req.user.role === 'barbeiro' && req.user.barberId) {
            query.set('barberId', req.user.barberId);
        }
        const path = query.toString() ? `/api/hoje?${query.toString()}` : '/api/hoje';
        const data = await backendFetch(path);
        res.json(data);
    } catch (e) {
        console.error('Erro /hoje:', e.message);
        res.status(503).json({ error: 'Backend offline', details: e.message });
    }
});

// Agendamentos futuros
app.get('/api/agendamentos', auth, async (req, res) => {
    try {
        // BUGFIX: repassar query params (data, busca, servico, page, pageSize) ao backend
        const queryParams = new URLSearchParams(req.query);
        
        if (req.user.role === 'barbeiro' && req.user.barberId) {
            queryParams.set('barberId', req.user.barberId);
        }

        const queryString = queryParams.toString();
        const path = queryString ? `/api/agendamentos?${queryString}` : '/api/agendamentos';
        const data = await backendFetch(path);
        res.json(data);
    } catch (e) {
        console.error('Erro /agendamentos:', e.message);
        res.status(503).json({ error: 'Backend offline' });
    }
});

// Semana
app.get('/api/semana', auth, async (req, res) => {
    try {
        let path = '/api/semana';
        if (req.user.role === 'barbeiro' && req.user.barberId) {
            path += `?barberId=${req.user.barberId}`;
        }
        const data = await backendFetch(path);
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

// Stats
app.get('/api/stats', auth, async (req, res) => {
    try {
        const query = new URLSearchParams(req.query);
        if (!query.has('days')) query.set('days', '30');

        if (req.user.role === 'barbeiro' && req.user.barberId) {
            query.set('barberId', req.user.barberId);
        }
        const path = `/api/stats?${query.toString()}`;
        const data = await backendFetch(path);
        res.json({
            ...data,
            uptime: Math.round((Date.now() - startTime) / 60000) + 'min',
            memoria: Math.round(process.memoryUsage().rss / 1024 / 1024) + 'MB',
            sessoes_ativas: sessions.size
        });
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

// HorÃ¡rios livres
app.get('/api/reports/appointments', auth, async (req, res) => {
    try {
        const query = new URLSearchParams(req.query);
        if (req.user.role === 'barbeiro' && req.user.barberId) {
            query.set('barberId', req.user.barberId);
        }
        if (!query.has('pageSize')) query.set('pageSize', '500');
        const data = await backendFetch(`/api/reports/appointments?${query.toString()}`);
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

// Status do bot WhatsApp
app.get('/api/bot/status', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/status');
        res.json(data);
    } catch (e) {
        res.json({
            status: 'offline',
            state: 'OFFLINE',
            connectionState: 'OFFLINE',
            whatsappConnected: false,
            bridgeReachable: false,
            botEnabled: false,
            queueSize: null,
            error: 'Bridge WhatsApp offline',
            details: e.message,
            timestamp: new Date().toISOString()
        });
    }
});

// Rota para simular mensagem recebida (Ãºtil para testes de fluxo)
app.post('/api/bot/simulate', auth, async (req, res) => {
    try {
        const phone = String(req.body.phone || req.body.from || '5511999999999').trim();
        const text = String(req.body.text || req.body.body || 'oi').trim();
        const pushname = String(req.body.pushname || req.body.pushName || 'Cliente Teste').trim();

        if (!phone || !text) {
            return res.status(400).json({ error: 'Informe phone e text para testar o fluxo.' });
        }

        if (req.body.viaBridge === true) {
            const data = await bridgeFetch(req, '/test/receive', {
                method: 'POST',
                body: JSON.stringify({ phone, text, pushname })
            });
            return res.json({ ...data, mode: 'bridge-queue', phone, text });
        }

        const data = await backendFetch('/api/webhook/whatsapp', {
            method: 'POST',
            body: JSON.stringify({
                phone,
                text,
                timestamp: Math.floor(Date.now() / 1000),
                storeId: req.user.storeId || 1,
                id: `dashboard-test-${Date.now()}`
            })
        });
        res.json({ ok: true, mode: 'backend-webhook', phone, text, pushname, result: data });
    } catch (e) {
        res.status(e.status || 500).json({ error: 'Erro ao simular mensagem', details: e.message, backendPath: e.backendPath });
    }
});

// QR Code do WhatsApp para parear pelo dashboard
app.get('/api/bot/qr', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/qr');
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Bridge WhatsApp offline', details: e.message });
    }
});

// Pareamento por cÃ³digo numÃ©rico (alternativa ao QR)
app.post('/api/bot/pairing-code', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/pairing-code', {
            method: 'POST',
            body: JSON.stringify(req.body)
        });
        res.json(data);
    } catch (e) {
        const status = e.status && e.status >= 400 && e.status < 500 ? e.status : 503;
        res.status(status).json({ error: e.data?.error || e.message || 'Bridge offline' });
    }
});

app.get('/api/bot/pairing-code/status', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/pairing-code/status');
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: e.data?.error || e.message || 'Bridge offline' });
    }
});

// Pausar ou ativar processamento do bot
app.post('/api/bot/toggle', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/bot/toggle', {
            method: 'POST',
            body: JSON.stringify({ enabled: req.body.enabled === true })
        });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Nao foi possivel atualizar o bot', details: e.message });
    }
});

// Recuperação isolada por loja: primeiro recicla apenas o cliente WhatsApp.
// Se a bridge filha não responde, reinicia somente o processo daquela loja via factory.
app.post('/api/bot/reconnect', auth, async (req, res) => {
    const storeId = Number(req.user?.storeId);
    if (!Number.isInteger(storeId) || storeId <= 0) {
        return res.status(400).json({ error: 'Loja da sessão não identificada.' });
    }

    try {
        const data = await bridgeFetch(req, '/reconnect', { method: 'POST', body: '{}' });
        return res.status(202).json({ ...data, mode: 'client', storeId });
    } catch (clientError) {
        try {
            const data = await factoryFetch(`/bridge/${storeId}/restart`, { method: 'POST', body: '{}' });
            return res.status(202).json({ ...data, mode: 'process', storeId, preserveSession: true });
        } catch (processError) {
            return res.status(503).json({
                error: 'Não foi possível recuperar a conexão do WhatsApp.',
                details: processError.message || clientError.message
            });
        }
    }
});

// Desconectar a sessao do WhatsApp
app.post('/api/bot/logout', auth, async (req, res) => {
    try {
        const data = await bridgeFetch(req, '/logout', { method: 'POST' });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Nao foi possivel desconectar o WhatsApp', details: e.message });
    }
});

// Templates das mensagens automaticas do WhatsApp
app.get('/api/bot/templates', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/settings');
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Erro ao carregar templates' });
    }
});

app.put('/api/bot/templates', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/settings', {
            method: 'POST',
            body: JSON.stringify(req.body),
            headers: { 'X-User-Role': req.user.role }
        });
        res.json(data);
    } catch (e) {
        res.status(500).json({ error: 'Erro ao salvar templates' });
    }
});

app.get('/api/horarios-livres', auth, async (req, res) => {
    try {
        const { data, servico, barberId } = req.query;
        if (!data || !servico) return res.status(400).json({ error: 'Informe data e servico' });
        const params = new URLSearchParams({ data, servico });
        if (barberId) params.set('barberId', barberId);
        const result = await backendFetch(`/api/horarios-livres?${params.toString()}`);
        res.json(result);
    } catch (e) {
        // NÃ£o mascarar erros de validaÃ§Ã£o (400) do backend como "Backend offline".
        // Preserva a mensagem real (ex.: "Servico invalido") para o usuÃ¡rio entender.
        const status = e.status && e.status >= 400 && e.status < 500 ? e.status : 503;
        const error = status === 503 ? 'Backend offline' : (e.data?.error || e.message || 'Erro ao buscar horarios');
        res.status(status).json({ error });
    }
});

app.get('/api/dias-indisponiveis', auth, async (req, res) => {
    try {
        const params = new URLSearchParams(req.query);
        const result = await backendFetch(`/api/dias-indisponiveis?${params.toString()}`);
        res.json(result);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message || 'Backend offline' });
    }
});

app.post('/api/dias-indisponiveis', auth, async (req, res) => {
    try {
        const body = { ...req.body };
        if (req.user.role === 'barbeiro' && req.user.barberId) body.BarberId = req.user.barberId;
        const result = await backendFetch('/api/dias-indisponiveis', {
            method: 'POST',
            body: JSON.stringify(body)
        });
        res.json(result);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message || 'Backend offline' });
    }
});

app.delete('/api/dias-indisponiveis/:id', auth, async (req, res) => {
    try {
        const result = await backendFetch(`/api/dias-indisponiveis/${req.params.id}`, { method: 'DELETE' });
        res.json(result);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message || 'Backend offline' });
    }
});

// Cancelar agendamento
// Criar agendamento manual pelo dashboard.
// BUGFIX: endpoint ausente impossibilitava salvar clientes/agendamentos pelo site
app.post('/api/agendamentos', auth, async (req, res) => {
    try {
        const body = { ...req.body };
        if (req.user.role === 'barbeiro' && req.user.barberId) {
            body.barberId = req.user.barberId;
        }

        const result = await backendFetch('/api/agendamentos', {
            method: 'POST',
            body: JSON.stringify(body)
        });
        res.json(result);
    } catch (e) {
        console.error('Erro POST /agendamentos:', e.message);
        res.status(503).json({ error: e.message || 'Backend offline' });
    }
});

app.delete('/api/agendamentos/:id', auth, async (req, res) => {
    try {
        if (!await ensureBarberOwnsAppointment(req, req.params.id)) {
            return res.status(403).json({ error: 'Este agendamento pertence a outro profissional' });
        }
        const result = await backendFetch(`/api/agendamentos/${req.params.id}`, {
            method: 'DELETE'
        });
        console.log(`Agendamento ${req.params.id} cancelado`);
        res.json(result);
    } catch (e) {
        res.status(500).json({ error: 'Erro ao cancelar' });
    }
});

// Confirmar presenÃ§a (PATCH rÃ¡pido)
app.patch('/api/agendamentos/:id/confirmar', auth, async (req, res) => {
    try {
        if (!await ensureBarberOwnsAppointment(req, req.params.id)) {
            return res.status(403).json({ error: 'Este agendamento pertence a outro profissional' });
        }
        const result = await backendFetch(`/api/agendamentos/${req.params.id}/confirmar`, {
            method: 'PATCH'
        });
        res.json(result);
    } catch (e) {
        res.status(500).json({ error: 'Erro ao confirmar presenÃ§a' });
    }
});

// Editar agendamento (PATCH)
app.patch('/api/agendamentos/:id', auth, async (req, res) => {
    try {
        if (!await ensureBarberOwnsAppointment(req, req.params.id)) {
            return res.status(403).json({ error: 'Este agendamento pertence a outro profissional' });
        }
        const result = await backendFetch(`/api/agendamentos/${req.params.id}`, {
            method: 'PATCH',
            body: JSON.stringify(req.body)
        });
        console.log(`Agendamento ${req.params.id} editado`);
        res.json(result);
    } catch (e) {
        res.status(500).json({ error: 'Erro ao editar' });
    }
});

// Download planilha sempre gerada pelo backend no caminho persistente
app.get('/api/export', auth, async (req, res) => {
    try {
        const query = req.query.refresh === '0' ? '?refresh=false' : '?refresh=true';
        const fetch = (await import('node-fetch')).default;
        const timeout = withTimeout(60000);
        const upstream = await fetch(`${BACKEND_URL}/api/export${query}`, {
            signal: timeout.signal,
            headers: {
                'X-API-KEY': BACKEND_API_KEY.trim(),
                'Authorization': req.headers.authorization || '',
                'ngrok-skip-browser-warning': 'true'
            }
        });
        timeout.done();

        if (!upstream.ok) {
            const data = await upstream.json().catch(() => ({}));
            return res.status(upstream.status).json({ error: data.error || 'Erro ao gerar planilha.' });
        }

        res.status(upstream.status);
        upstream.headers.forEach((value, key) => {
            if (!['transfer-encoding', 'content-encoding'].includes(key.toLowerCase())) res.setHeader(key, value);
        });
        res.setHeader('Content-Disposition', upstream.headers.get('content-disposition') || 'attachment; filename="agendamentos.xlsx"');
        upstream.body.pipe(res);
    } catch (e) {
        res.status(504).json({ error: 'Timeout ao gerar planilha', details: e.message });
    }
});

app.post('/api/google-sheets/sync', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/google-sheets/sync', { method: 'POST' });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Erro ao sincronizar Google Sheets' });
    }
});

app.post('/api/spreadsheets/update', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/spreadsheets/update?syncGoogleSheets=true', { method: 'POST' });
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: 'Erro ao atualizar planilhas', details: e.message });
    }
});

app.get('/api/spreadsheets/status', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/spreadsheets/status');
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: 'Erro ao verificar planilha', details: e.message });
    }
});

app.get('/api/google-sheets/status', auth, async (req, res) => {
    try {
        const settings = await backendFetch('/api/settings');
        const webhookUrl = settings.GoogleSheets_WebhookUrl || '';
        let webhookHost = null;
        try {
            webhookHost = webhookUrl ? new URL(webhookUrl).host : null;
        } catch {}

        res.json({
            configured: /^https:\/\/script\.google\.com\/macros\/s\/.+\/exec/i.test(webhookUrl),
            hasWebhook: Boolean(webhookUrl),
            webhookHost
        });
    } catch (e) {
        res.status(e.status || 503).json({ error: 'Erro ao verificar Google Sheets', details: e.message });
    }
});

app.post('/api/maintenance/safe-cleanup', auth, async (req, res) => {
    if (req.user.role === 'barbeiro') {
        return res.status(403).json({ error: 'Apenas administradores podem executar a limpeza segura.' });
    }

    const result = {
        ok: true,
        backend: null,
        bridge: null,
        dashboard: {
            actions: ['Notificacoes visuais da sessao atual podem ser limpas pela dashboard']
        },
        preserved: [
            'Banco de dados',
            'usuarios e senhas',
            'lojas/empresas',
            'sessao do WhatsApp',
            'agendamentos',
            'configuracoes',
            'planilhas'
        ]
    };

    try {
        result.backend = await backendFetch('/api/maintenance/safe-cleanup', { method: 'POST' });
    } catch (e) {
        result.ok = false;
        result.backend = { ok: false, error: e.message };
    }

    try {
        result.bridge = await bridgeFetch(req, '/messages/pending/clear', { method: 'POST' });
    } catch (e) {
        result.ok = false;
        result.bridge = { ok: false, error: e.message };
    }

    res.status(result.ok ? 200 : 207).json(result);
});

app.get('/api/modules', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/modules');
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: 'Erro ao carregar modulos', details: e.message });
    }
});

app.get('/api/diagnostics', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/diagnostics');
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({
            error: e.data?.error || e.message || 'Erro ao carregar diagnostico operacional',
            details: e.data?.details || e.data?.traceId
        });
    }
});

// Gerenciamento de Barbeiros
app.get('/api/barbeiros', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/barbeiros');
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

app.post('/api/barbeiros', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/barbeiros', {
            method: 'POST',
            body: JSON.stringify(req.body),
            headers: { 'X-User-Role': req.user.role }
        });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

app.delete('/api/barbeiros/:id', auth, async (req, res) => {
    try {
        // Impede que o usuÃ¡rio exclua a si mesmo comparando o ID da sessÃ£o com o ID da requisiÃ§Ã£o
        if (req.user.barberId && req.user.barberId.toString() === req.params.id) {
            return res.status(403).json({ 
                error: 'AÃ§Ã£o bloqueada: VocÃª nÃ£o pode excluir seu prÃ³prio perfil profissional enquanto estiver logado.' 
            });
        }

        const data = await backendFetch(`/api/barbeiros/${req.params.id}`, {
            method: 'DELETE'
        });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

app.patch('/api/barbeiros/:id', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/barbeiros/${req.params.id}`, {
            method: 'PATCH',
            body: JSON.stringify(req.body),
            headers: { 'X-User-Role': req.user.role }
        });
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: 'Backend offline' });
    }
});

// Grade semanal de horÃ¡rios do profissional (entrada/saÃ­da por dia da semana).
// Sem estas rotas o dashboard recebia 404 ao abrir/salvar os horÃ¡rios do barbeiro.
app.get('/api/barbeiros/:id/horarios', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/barbeiros/${req.params.id}/horarios`);
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

app.put('/api/barbeiros/:id/horarios', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/barbeiros/${req.params.id}/horarios`, {
            method: 'PUT',
            body: JSON.stringify(req.body),
            headers: { 'X-User-Role': req.user.role }
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Erro ao salvar horarios' });
    }
});

// ConfiguraÃ§Ãµes Gerais
app.get('/api/settings', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/settings');
        res.json(data);
    } catch (e) {
        const status = e.status || 503;
        res.status(status).json({ error: e.message });
    }
});

app.put('/api/settings', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/settings', {
            method: 'POST',
            body: JSON.stringify(req.body),
            headers: { 'X-User-Role': req.user.role }
        });
        res.json(data);
    } catch (e) {
        const status = e.status || 503;
        res.status(status).json({ error: e.data?.error || e.message || 'Erro ao salvar configuracoes' });
    }
});

// ============ ROTAS SUPERADMIN ============
function superauth(req, res, next) {
    auth(req, res, () => {
        if (req.user.role !== 'superadmin') return res.status(403).json({ error: 'Acesso restrito' });
        // Superadmin: storeId=0 sinaliza ao backend que pode ver todas as lojas
        requestContext.run({ storeId: 0 }, next);
    });
}

// Aliases para compatibilidade direta com requisiÃ§Ãµes do Superadmin
app.get('/superadmin/global-stats', superauth, (req, res) => res.redirect('/api/superadmin/global-stats'));
app.get('/superadmin/overview', superauth, (req, res) => res.redirect('/api/superadmin/overview'));
app.get('/superadmin/subscriptions', superauth, (req, res) => res.redirect('/api/superadmin/subscriptions'));
app.get('/superadmin/stores', superauth, (req, res) => res.redirect('/api/superadmin/stores'));
app.get('/superadmin/stores/:id/users', superauth, (req, res) => res.redirect(`/api/superadmin/stores/${req.params.id}/users`));

app.get('/api/superadmin/global-stats', superauth, async (req, res) => {
    try {
        const globalData = await backendFetch('/api/superadmin/global-stats');
        const bridgeStatus = await bridgeFetch(req, '/status').catch(() => ({ status: 'offline' }));
        
        res.json({
            ...globalData,
            infrastructure: {
                mainBridge: bridgeStatus,
                apiUptimeFormatted: Math.round((Date.now() - startTime) / 60000) + 'm',
                activeSessions: sessions.size
            }
        });
    } catch (e) { 
        console.error('Superadmin Stats Error:', e);
        res.status(e.status || 500).json({ 
            error: e.message || 'Falha ao processar estatÃ­sticas globais' 
        });
    }
});

app.get('/api/superadmin/overview', superauth, async (req, res) => {
    try {
        const overview = await backendFetch('/api/superadmin/overview');
        res.json({
            ...overview,
            proxy: {
                generatedAt: new Date().toISOString(),
                apiUptimeFormatted: Math.round((Date.now() - startTime) / 60000) + 'm',
                activeSessions: sessions.size
            }
        });
    } catch (e) {
        console.error('Superadmin Overview Error:', {
            message: e.message,
            status: e.status,
            backendPath: e.backendPath,
            data: e.data
        });
        res.status(e.status || 500).json({
            error: e.data?.error || e.message || 'Falha ao carregar visao geral do Super Admin',
            backendPath: e.backendPath,
            backendStatus: e.status
        });
    }
});

app.get('/api/superadmin/subscriptions', superauth, async (req, res) => {
    try {
        const data = await backendFetch('/api/superadmin/subscriptions');
        res.json(data);
    } catch (e) {
        console.error('Superadmin Subscriptions Error:', {
            message: e.message,
            status: e.status,
            backendPath: e.backendPath,
            data: e.data
        });
        res.status(e.status || 500).json({
            error: e.data?.error || e.message || 'Falha ao carregar assinaturas do Super Admin',
            backendPath: e.backendPath,
            backendStatus: e.status
        });
    }
});

app.post('/api/superadmin/subscriptions/:id/activate', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/subscriptions/${req.params.id}/activate`, {
            method: 'POST',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Falha ao ativar assinatura' });
    }
});

app.post('/api/superadmin/subscriptions/:id/cancel', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/subscriptions/${req.params.id}/cancel`, {
            method: 'POST',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Falha ao cancelar assinatura' });
    }
});

// Helper para buscar status de um Bridge/Backend especÃ­fico
function normalizeServiceUrl(url, fallback) {
    const value = (url || fallback || '').trim();
    if (!value) return '';
    return value.replace(/\/+$/, '');
}

async function fetchServiceStatus(url, type) {
    const fetch = (await import('node-fetch')).default;
    const baseUrl = normalizeServiceUrl(url, type === 'bridge' ? DEFAULT_BRIDGE_URL : BACKEND_URL);
    if (!baseUrl) return { status: 'offline', detail: 'URL nao configurada', url: '' };

    const timeout = withTimeout(5000);
    try {
        const res = await fetch(baseUrl + (type === 'bridge' ? '/status' : '/health'), {
            signal: timeout.signal,
            headers: type === 'bridge' ? { 'X-API-KEY': BACKEND_API_KEY.trim() } : {}
        });

        if (!res.ok) {
            return { status: 'offline', detail: `HTTP ${res.status}`, url: baseUrl };
        }

        const data = await res.json().catch(() => ({}));
        const bridgeConnected = data.whatsappConnected === true || data.connectionState === 'ONLINE' || data.state === 'ONLINE';

        return {
            status: type === 'bridge' ? (bridgeConnected ? 'online' : 'attention') : 'online',
            detail: type === 'bridge'
                ? (bridgeConnected ? 'WhatsApp conectado' : (data.connectionState || data.state || data.status || 'Bridge respondendo'))
                : 'API respondendo',
            url: baseUrl
        };
    } catch (e) {
        return { status: 'offline', detail: e.name === 'AbortError' ? 'Timeout' : e.message, url: baseUrl };
    } finally {
        timeout.done();
    }
}

app.get('/api/superadmin/stores', superauth, async (req, res) => {
    try {
        const data = await backendFetch('/api/superadmin/stores');
        
        // Para cada loja, tentar buscar o status do Bridge e Backend
        const storesWithStatus = await Promise.all(data.map(async rawStore => {
            const store = enrichStorePayload(rawStore);
            const bridgeCheck = await fetchServiceStatus(store.bridgeUrl, 'bridge');
            const backendCheck = await fetchServiceStatus(store.backendUrl, 'backend');

            return {
                ...store,
                bridgeUrl: store.bridgeUrl || bridgeCheck.url,
                backendUrl: store.backendUrl || backendCheck.url,
                bridgeStatus: bridgeCheck.status,
                backendStatus: backendCheck.status,
                bridgeStatusDetail: bridgeCheck.detail,
                backendStatusDetail: backendCheck.detail,
                bridgeCheckedUrl: bridgeCheck.url,
                backendCheckedUrl: backendCheck.url
            };
        }));
        res.json(storesWithStatus);
    } catch (e) {
        console.error('Superadmin Stores Error:', {
            message: e.message,
            status: e.status,
            backendPath: e.backendPath,
            data: e.data
        });
        res.status(e.status || 500).json({ 
            error: e.data?.error || e.message || 'Falha na comunicacao com o servidor principal',
            backendPath: e.backendPath,
            backendStatus: e.status,
            details: e.data?.details || e.data?.traceId || undefined
        });
    }
});

// 4.7: Export/backup isolado dos dados de uma loja (download JSON)
app.get('/api/superadmin/stores/:id/export', superauth, async (req, res) => {
    try {
        const fetch = (await import('node-fetch')).default;
        const timeout = withTimeout(30000);
        const upstream = await fetch(`${BACKEND_URL}/api/superadmin/stores/${req.params.id}/export`, {
            signal: timeout.signal,
            headers: { 'X-API-KEY': BACKEND_API_KEY.trim() }
        });
        timeout.done();

        if (!upstream.ok) {
            const data = await upstream.json().catch(() => ({}));
            return res.status(upstream.status).json({ error: data.error || 'Erro ao exportar loja' });
        }

        const disposition = upstream.headers.get('content-disposition') || `attachment; filename="export-loja-${req.params.id}.json"`;
        res.setHeader('Content-Disposition', disposition);
        res.setHeader('Content-Type', 'application/json');
        upstream.body.pipe(res);
    } catch (e) {
        res.status(504).json({ error: 'Timeout ao exportar dados da loja', details: e.message });
    }
});

app.get('/api/superadmin/stores/:id/users', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}/users`);
        res.json(data);
    } catch (e) {
        console.error('Erro ao buscar usuarios da loja:', e.message);
        res.status(500).json({ error: 'Erro ao carregar usuarios' });
    }
});

app.post('/api/superadmin/stores/:id/admins', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}/admins`, {
            method: 'POST',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao criar administrador da loja' });
    }
});

app.patch('/api/superadmin/users/:id', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/users/${req.params.id}`, {
            method: 'PATCH',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao atualizar usuario' });
    }
});

app.delete('/api/superadmin/users/:id', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/users/${req.params.id}`, {
            method: 'DELETE'
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao remover usuario' });
    }
});

app.get('/api/superadmin/stores/:id', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}`);
        res.json(data);
    } catch (e) {
        console.error('Erro ao carregar loja:', e.message);
        res.status(500).json({ error: 'Erro ao carregar loja' });
    }
});

app.post('/api/superadmin/stores', superauth, async (req, res) => {
    try {
        const created = await backendFetch('/api/superadmin/stores', {
            method: 'POST',
            body: JSON.stringify(req.body)
        });
        const data = enrichStorePayload(created);
        const newStoreId = data?.storeId || data?.id;
        let bridgeStart = null;
        // Inicia bridge da nova loja diretamente (mais rÃ¡pido que aguardar o /sync geral)
        if (newStoreId) {
            try {
                bridgeStart = await factoryFetch(`/bridge/${newStoreId}/start`, { method: 'POST', body: '{}' });
                console.log(`[Superadmin] Bridge da loja ${newStoreId} iniciada automaticamente.`);
            } catch (bridgeErr) {
                console.warn(`[Superadmin] Bridge nÃ£o pÃ´de ser iniciada para loja ${newStoreId}: ${bridgeErr.message}`);
            }
        }
        // Sincroniza todas as bridges para garantir consistÃªncia
        try {
            await factoryFetch('/sync', { method: 'POST', body: '{}' });
        } catch (syncErr) {
            console.warn(`[Superadmin] Loja criada, mas sync da bridge falhou: ${syncErr.message}`);
        }
        let storesMeta = null;
        try {
            storesMeta = await syncStoresMetaFromBackend('store-created');
        } catch (metaErr) {
            console.warn(`[Superadmin] Loja criada, mas stores-meta.json nao foi sincronizado: ${metaErr.message}`);
        }
        res.json({ ...data, bridgeStart, storesMetaSynced: Boolean(storesMeta?.ok), storesMetaCount: storesMeta?.count });
    } catch (e) {
        console.error(`[Superadmin] Falha ao criar loja: ${e.message}`);
        const status = e.status || 500;
        res.status(status).json({
            error: e.data?.error || e.message || 'Erro interno ao processar criaÃ§Ã£o'
        });
    }
});

async function updateStore(req, res) {
    try {
        const updated = await backendFetch(`/api/superadmin/stores/${req.params.id}`, {
            method: 'PUT',
            body: JSON.stringify(req.body)
        });
        const data = enrichStorePayload(updated);
        try {
            await factoryFetch('/sync', { method: 'POST', body: '{}' });
        } catch (syncErr) {
            console.warn(`[Superadmin] Loja atualizada, mas sync da bridge falhou: ${syncErr.message}`);
        }
        let storesMeta = null;
        try {
            storesMeta = await syncStoresMetaFromBackend('store-updated');
        } catch (metaErr) {
            console.warn(`[Superadmin] Loja atualizada, mas stores-meta.json nao foi sincronizado: ${metaErr.message}`);
        }
        res.json({ ...data, storesMetaSynced: Boolean(storesMeta?.ok), storesMetaCount: storesMeta?.count });
    } catch (e) {
        console.error('Erro ao atualizar loja:', e.message);
        const status = e.status || 500;
        res.status(status).json({ error: e.data?.error || e.message || 'Erro ao atualizar loja' });
    }
}
app.patch('/api/superadmin/stores/:id', superauth, updateStore);
app.put('/api/superadmin/stores/:id',   superauth, updateStore);

app.post('/api/superadmin/stores/:id/subscription/mark-paid', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}/subscription/mark-paid`, {
            method: 'POST',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao confirmar pagamento da loja' });
    }
});

app.get('/api/superadmin/stores/:id/payments', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}/payments`);
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao carregar historico de pagamentos' });
    }
});

app.patch('/api/superadmin/stores/:id/modules/:moduleKey', superauth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}/modules/${encodeURIComponent(req.params.moduleKey)}`, {
            method: 'PATCH',
            body: JSON.stringify(req.body || {})
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao atualizar modulo da loja' });
    }
});

app.delete('/api/superadmin/stores/:id', superauth, async (req, res) => {
    try {
        const storeId = Number(req.params.id);
        const data = await backendFetch(`/api/superadmin/stores/${req.params.id}`, {
            method: 'DELETE'
        });
        let bridgeStopped = false;
        if (storeId) {
            try {
                await factoryFetch(`/bridge/${storeId}/stop`, { method: 'POST', body: '{}' });
                bridgeStopped = true;
            } catch (bridgeErr) {
                console.warn(`[Superadmin] Loja excluida, mas bridge ${storeId} nao parou automaticamente: ${bridgeErr.message}`);
            }
        }
        try {
            await factoryFetch('/sync', { method: 'POST', body: '{}' });
        } catch (syncErr) {
            console.warn(`[Superadmin] Loja excluida, mas sync da bridge falhou: ${syncErr.message}`);
        }
        let storesMeta = null;
        try {
            storesMeta = await syncStoresMetaFromBackend('store-deleted');
        } catch (metaErr) {
            console.warn(`[Superadmin] Loja excluida, mas stores-meta.json nao foi sincronizado: ${metaErr.message}`);
        }
        res.json({ ...data, bridgeStopped, storesMetaSynced: Boolean(storesMeta?.ok), storesMetaCount: storesMeta?.count });
    } catch (e) {
        console.error(`[Superadmin] Falha ao excluir loja ${req.params.id}: ${e.message}`);
        res.status(e.status || 500).json({ error: e.data?.error || e.message || 'Erro ao excluir loja' });
    }
});

// ============ BRIDGE FACTORY â€” gerenciamento multi-instÃ¢ncia ============

async function factoryFetch(path, options = {}) {
    const nodeFetch = (await import('node-fetch')).default;
    const timeout = withTimeout(15000);
    try {
        const res = await nodeFetch(`${FACTORY_URL}${path}`, {
            ...options,
            signal: timeout.signal,
            headers: {
                'X-API-KEY': BACKEND_API_KEY.trim(),
                'Content-Type': 'application/json',
                ...options.headers
            }
        });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) {
            const err = new Error(data.error || `Factory retornou ${res.status}`);
            err.status = res.status;
            throw err;
        }
        return data;
    } finally {
        timeout.done();
    }
}

// Status geral de todas as bridges (superadmin)
app.get('/api/superadmin/bridges', superauth, async (req, res) => {
    try {
        const data = await factoryFetch('/status');
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message || 'Factory offline' });
    }
});

// Status de uma bridge especÃ­fica (superadmin ou admin da loja)
app.get('/api/superadmin/bridges/:storeId/status', superauth, async (req, res) => {
    try {
        const data = await factoryFetch(`/bridge/${req.params.storeId}/status`);
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message });
    }
});

// QR code de uma bridge especÃ­fica (superadmin)
app.get('/api/superadmin/bridges/:storeId/qr', superauth, async (req, res) => {
    try {
        const data = await factoryFetch(`/bridge/${req.params.storeId}/qr`);
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.message });
    }
});

// Iniciar bridge de uma loja (superadmin)
app.post('/api/superadmin/bridges/:storeId/start', superauth, async (req, res) => {
    try {
        const data = await factoryFetch(`/bridge/${req.params.storeId}/start`, { method: 'POST', body: '{}' });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.message });
    }
});

// Parar bridge de uma loja (superadmin)
app.post('/api/superadmin/bridges/:storeId/stop', superauth, async (req, res) => {
    try {
        const data = await factoryFetch(`/bridge/${req.params.storeId}/stop`, { method: 'POST', body: '{}' });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.message });
    }
});

// Reiniciar bridge de uma loja (superadmin)
app.post('/api/superadmin/bridges/:storeId/restart', superauth, async (req, res) => {
    try {
        const data = await factoryFetch(`/bridge/${req.params.storeId}/restart`, { method: 'POST', body: '{}' });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.message });
    }
});

// Sincronizar todas as bridges com o backend (superadmin â€” Ãºtil apÃ³s criar lojas)
app.post('/api/superadmin/bridges/sync', superauth, async (req, res) => {
    try {
        const data = await factoryFetch('/sync', { method: 'POST', body: '{}' });
        res.json(data);
    } catch (e) {
        res.status(e.status || 500).json({ error: e.message });
    }
});

// Status da bridge da loja atual (admin normal via session)
app.get('/api/bot/status/:storeId', auth, async (req, res) => {
    const storeId = Number(req.params.storeId);
    // Somente superadmin pode ver bridge de qualquer loja; admin sÃ³ vÃª a sua
    if (req.user.role !== 'superadmin' && req.user.storeId !== storeId) {
        return res.status(403).json({ error: 'Acesso negado' });
    }
    try {
        const data = await factoryFetch(`/bridge/${storeId}/status`);
        res.json(data);
    } catch (e) {
        res.json({ status: 'offline', error: e.message });
    }
});

// QR da bridge da loja atual (admin via session)
app.get('/api/bot/qr/:storeId', auth, async (req, res) => {
    const storeId = Number(req.params.storeId);
    if (req.user.role !== 'superadmin' && req.user.storeId !== storeId) {
        return res.status(403).json({ error: 'Acesso negado' });
    }
    try {
        const data = await factoryFetch(`/bridge/${storeId}/qr`);
        res.json(data);
    } catch (e) {
        res.status(503).json({ error: e.message });
    }
});

// ============ ROTAS CRM AVANCADO (por tenant) ============

app.use('/api/customers', auth, async (req, res) => {
    await forwardOptimizationRequest(req, res, '/api/customers', '/api/customers');
});

app.use('/api/customer-tags', auth, async (req, res) => {
    await forwardOptimizationRequest(req, res, '/api/customer-tags', '/api/customer-tags');
});

app.use('/api/customer-reminders', auth, async (req, res) => {
    await forwardOptimizationRequest(req, res, '/api/customer-reminders', '/api/customer-reminders');
});

// ============ ROTAS OTIMIZACAO DE COMPUTADORES (por tenant) ============

async function forwardOptimizationRequest(req, res, publicPrefix, backendPrefix) {
    try {
        const suffix = req.originalUrl.replace(new RegExp(`^${publicPrefix}`), '');
        const data = await backendFetch(`${backendPrefix}${suffix}`, {
            method: req.method,
            body: ['GET', 'HEAD'].includes(req.method) ? undefined : JSON.stringify(req.body || {}),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.status(req.method === 'POST' ? 201 : 200).json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
}

app.use('/api/optimization', auth, async (req, res) => {
    await forwardOptimizationRequest(req, res, '/api/optimization', '/api/optimization');
});

app.use('/api/tech', auth, async (req, res) => {
    await forwardOptimizationRequest(req, res, '/api/tech', '/api/optimization');
});

// ============ ROTAS SERVIÃ‡OS (por tenant) ============

app.get('/api/servicos', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/servicos', {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

app.post('/api/servicos', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/servicos', {
            method: 'POST',
            body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.status(201).json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

app.patch('/api/servicos/:id', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/servicos/${req.params.id}`, {
            method: 'PATCH',
            body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

app.delete('/api/servicos/:id', auth, async (req, res) => {
    try {
        await backendFetch(`/api/servicos/${req.params.id}`, {
            method: 'DELETE',
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.status(204).end();
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

// ============ ASSINATURAS ============

app.get('/api/assinaturas/planos', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/assinaturas/planos', {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.post('/api/assinaturas/planos', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/assinaturas/planos', {
            method: 'POST', body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.status(201).json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.patch('/api/assinaturas/planos/:id', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/assinaturas/planos/${req.params.id}`, {
            method: 'PATCH', body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.delete('/api/assinaturas/planos/:id', auth, async (req, res) => {
    try {
        await backendFetch(`/api/assinaturas/planos/${req.params.id}`, {
            method: 'DELETE',
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.status(204).end();
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.get('/api/assinaturas', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/assinaturas', {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.get('/api/assinaturas/stats', auth, async (req, res) => {
    try {
        const data = await backendFetch('/api/assinaturas/stats', {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.delete('/api/assinaturas/canceladas', auth, async (req, res) => {
    if (req.user.role === 'barbeiro') return res.status(403).json({ error: 'Acesso restrito' });
    try {
        const data = await backendFetch('/api/assinaturas/canceladas', {
            method: 'DELETE',
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.delete('/api/assinaturas/:id', auth, async (req, res) => {
    if (req.user.role === 'barbeiro') return res.status(403).json({ error: 'Acesso restrito' });
    try {
        const data = await backendFetch(`/api/assinaturas/${req.params.id}`, {
            method: 'DELETE',
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.post('/api/assinaturas/:id/ativar', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/assinaturas/${req.params.id}/ativar`, {
            method: 'POST',
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.post('/api/assinaturas/:id/cancelar', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/assinaturas/${req.params.id}/cancelar`, {
            method: 'POST', body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.patch('/api/assinaturas/:id/notas', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/assinaturas/${req.params.id}/notas`, {
            method: 'PATCH', body: JSON.stringify(req.body),
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.get('/api/assinaturas/lookup', auth, async (req, res) => {
    try {
        const qs = req.query.phone ? `?phone=${encodeURIComponent(req.query.phone)}` : '';
        const data = await backendFetch(`/api/assinaturas/lookup${qs}`, {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) { res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' }); }
});

app.get('/api/pix/subscription/:id', auth, async (req, res) => {
    try {
        const data = await backendFetch(`/api/pix/subscription/${req.params.id}`, {
            headers: { 'Authorization': req.headers['authorization'] || '' }
        });
        res.json(data);
    } catch (e) {
        res.status(e.status || 503).json({ error: e.data?.error || e.message || 'Backend offline' });
    }
});

// ============ ROTAS PÃšBLICAS DE AGENDAMENTO (sem autenticaÃ§Ã£o) ============

app.get('/api/public/pix/qrcode', async (req, res) => {
    const payload = String(req.query.payload || '');
    if (!payload.trim()) return res.status(400).json({ error: 'Payload PIX ausente.' });
    if (payload.length > 512) return res.status(400).json({ error: 'Payload PIX muito grande.' });

    const nodeFetch = (await import('node-fetch')).default;
    const timeout = withTimeout(10000);
    try {
        const upstream = await nodeFetch(`${BACKEND_URL}/api/public/pix/qrcode?payload=${encodeURIComponent(payload)}`, {
            signal: timeout.signal
        });
        const body = Buffer.from(await upstream.arrayBuffer());
        res.status(upstream.status);
        res.setHeader('Content-Type', upstream.headers.get('content-type') || 'image/png');
        res.setHeader('Cache-Control', 'no-store');
        res.send(body);
    } catch (e) {
        res.status(503).json({ error: e.name === 'AbortError' ? 'Timeout ao gerar QR Code PIX' : 'Backend offline' });
    } finally {
        timeout.done();
    }
});

const publicBookingLimiter = rateLimit({
    windowMs: 15 * 60 * 1000,
    max: 30,
    standardHeaders: true,
    legacyHeaders: false,
    message: { error: 'Muitas tentativas de agendamento. Aguarde 15 minutos.' }
});

// Info pÃºblica de uma loja (slug â†’ nome, serviÃ§os, horÃ¡rios)
app.get('/api/public/store/:slug', (req, res) => {
    const meta = loadStoresMeta();
    const store = meta[req.params.slug];
    if (!store) return res.status(404).json({ error: 'Loja nÃ£o encontrada' });
    const { storeId, bridgeUrl, backendUrl, bridgePort, ...publicData } = store;
    res.json(publicData);
});

// Agendamento pÃºblico â€” recebe do site do Lavajato/Barbearia sem autenticaÃ§Ã£o
app.post('/api/public/booking', publicBookingLimiter, async (req, res) => {
    const { nome, telefone, servico, data, hora, storeSlug, veiculo } = req.body || {};

    if (!nome || !telefone || !servico || !data || !hora || !storeSlug) {
        return res.status(400).json({ error: 'Campos obrigatÃ³rios: nome, telefone, servico, data, hora, storeSlug.' });
    }

    const phoneClean = String(telefone).replace(/\D/g, '');
    if (phoneClean.length < 10) {
        return res.status(400).json({ error: 'Telefone invÃ¡lido. Informe o DDD + nÃºmero.' });
    }

    const meta = loadStoresMeta();
    const storeCfg = meta[storeSlug];
    if (!storeCfg) return res.status(404).json({ error: 'Loja nÃ£o encontrada para este slug.' });

    // Valida que o serviÃ§o pertence Ã  loja
    if (storeCfg.services && storeCfg.services.length) {
        const valid = storeCfg.services.some(s => s.name === servico || s.id === servico);
        if (!valid) return res.status(400).json({ error: 'ServiÃ§o nÃ£o disponÃ­vel nesta loja.' });
    }

    try {
        const result = await backendFetchForStore(storeCfg.storeId, '/api/agendamentos', {
            method: 'POST',
            body: JSON.stringify({
                NomeCliente: nome.trim(),
                Telefone: phoneClean,
                Servico: servico,
                Data: data,
                Hora: hora,
                Observacao: veiculo ? `VeÃ­culo: ${veiculo.trim()}` : '',
                Fonte: `site-${storeSlug}`
            })
        });
        res.json({ ok: true, message: 'Agendamento registrado com sucesso!', appointment: result });
    } catch (e) {
        const status = e.status || 503;
        const msg = e.data?.error || e.message || 'ServiÃ§o temporariamente indisponÃ­vel.';
        console.error(`[PublicBooking] Erro ao criar agendamento para ${storeSlug}:`, msg);
        res.status(status).json({ error: msg });
    }
});

// ============ SETUP INICIAL DO LAVAJATO (superadmin) ============

// Cria a loja Lavajato no backend .NET a partir do stores-meta.json
app.post('/api/superadmin/setup/lavajato', superauth, async (req, res) => {
    const meta = loadStoresMeta();
    const cfg = meta['lavajato-paulinho'];
    if (!cfg) return res.status(404).json({ error: 'ConfiguraÃ§Ã£o nÃ£o encontrada em data/stores-meta.json' });

    const adminPassword = req.body?.adminPassword;
    if (!adminPassword) {
        return res.status(400).json({ error: 'Informe adminPassword para criar o usuario admin da loja.' });
    }

    try {
        const data = await backendFetch('/api/superadmin/stores', {
            method: 'POST',
            body: JSON.stringify({
                Name: cfg.name,
                Slug: cfg.slug,
                Plan: cfg.plan,
                BusinessType: cfg.businessType,
                BridgeUrl: cfg.bridgeUrl,
                BackendUrl: cfg.backendUrl,
                AdminUsername: 'admin-lavajato',
                AdminPassword: adminPassword
            })
        });

        // Atualiza storeId no arquivo local com o ID retornado pelo backend
        const newId = data.storeId ?? data.id ?? cfg.storeId;
        meta['lavajato-paulinho'] = { ...cfg, storeId: newId };
        fs.writeFileSync(STORES_META_FILE, JSON.stringify(meta, null, 2), 'utf8');

        // Sincroniza Bridge Factory para que o bridge do storeId 2 seja registrado
        try { await factoryFetch('/sync', { method: 'POST', body: '{}' }); } catch {}

        res.json({ ok: true, storeId: newId, message: 'Loja Lavajato criada com sucesso!', store: data });
    } catch (e) {
        const status = e.status || 500;
        res.status(status).json({ error: e.data?.error || e.message || 'Erro ao criar loja' });
    }
});

// Aplica templates WhatsApp especÃ­ficos do Lavajato (carwash) na loja storeId configurado
app.post('/api/superadmin/setup/lavajato/templates', superauth, async (req, res) => {
    const meta = loadStoresMeta();
    const cfg = meta['lavajato-paulinho'];
    if (!cfg) return res.status(404).json({ error: 'Config do Lavajato nÃ£o encontrada' });

    const templatesPath = path.join(DATA_DIR, 'whatsapp-templates-lavajato.json');
    if (!fs.existsSync(templatesPath)) {
        return res.status(404).json({ error: 'Arquivo data/whatsapp-templates-lavajato.json nÃ£o encontrado' });
    }

    const templates = JSON.parse(fs.readFileSync(templatesPath, 'utf8'));
    try {
        const result = await backendFetchForStore(cfg.storeId, '/api/settings', {
            method: 'POST',
            body: JSON.stringify(templates),
            headers: { 'X-User-Role': 'superadmin' }
        });
        res.json({ ok: true, message: 'Templates WhatsApp do Lavajato configurados!', storeId: cfg.storeId, result });
    } catch (e) {
        res.status(e.status || 500).json({ error: e.data?.error || e.message });
    }
});

// Inicia a Bridge WhatsApp do Lavajato (storeId configurado no meta)
app.post('/api/superadmin/setup/lavajato/bridge/start', superauth, async (req, res) => {
    const meta = loadStoresMeta();
    const cfg = meta['lavajato-paulinho'];
    if (!cfg) return res.status(404).json({ error: 'Config do Lavajato nÃ£o encontrada' });

    try {
        const data = await factoryFetch(`/bridge/${cfg.storeId}/start`, { method: 'POST', body: '{}' });
        res.json({ ok: true, storeId: cfg.storeId, bridgePort: cfg.bridgePort, ...data });
    } catch (e) {
        res.status(e.status || 500).json({ error: e.message || 'Falha ao iniciar bridge do Lavajato' });
    }
});

// Status da Bridge do Lavajato
app.get('/api/superadmin/setup/lavajato/bridge/status', superauth, async (req, res) => {
    const meta = loadStoresMeta();
    const cfg = meta['lavajato-paulinho'];
    if (!cfg) return res.status(404).json({ error: 'Config do Lavajato nÃ£o encontrada' });

    try {
        const data = await factoryFetch(`/bridge/${cfg.storeId}/status`);
        res.json({ storeId: cfg.storeId, bridgePort: cfg.bridgePort, ...data });
    } catch (e) {
        res.json({ storeId: cfg.storeId, status: 'offline', error: e.message });
    }
});

// 404
app.use((req, res) => res.status(404).json({
    error: 'Endpoint nao encontrado',
    disponiveis: [
        'POST /api/auth/login',
        'POST /api/auth/logout',
        'GET /api/hoje',
        'GET /api/agendamentos',
        'POST /api/agendamentos',
        'GET /api/dias-indisponiveis',
        'POST /api/dias-indisponiveis',
        'GET /api/semana',
        'GET /api/stats',
        'GET /api/bot/status',
        'GET /api/bot/qr',
        'POST /api/bot/toggle',
        'POST /api/bot/logout',
        'GET /api/bot/templates',
        'PUT /api/bot/templates',
        'GET /api/horarios-livres?data=&servico=',
        'DELETE /api/agendamentos/:id',
        'PATCH /api/agendamentos/:id',
        'GET /api/settings',
        'PUT /api/settings',
        'GET /api/barbeiros',
        'POST /api/barbeiros',
        'DELETE /api/barbeiros/:id',
        'GET /api/export',
        'GET /api/servicos',
        'POST /api/servicos',
        'PATCH /api/servicos/:id',
        'DELETE /api/servicos/:id',
        'GET /api/superadmin/subscriptions',
        'POST /api/superadmin/subscriptions/:id/activate',
        'POST /api/superadmin/subscriptions/:id/cancel',
        'PATCH /api/superadmin/stores/:id',
        'PUT /api/superadmin/stores/:id',
        'GET /api/public/store/:slug',
        'POST /api/public/booking',
        'POST /api/superadmin/setup/lavajato',
        'POST /api/superadmin/setup/lavajato/templates',
        'POST /api/superadmin/setup/lavajato/bridge/start',
        'GET /api/superadmin/setup/lavajato/bridge/status'
    ]
}));

const server = app.listen(PORT, HOST, () => {
    console.log(`\nNythar - Dashboard & Chatbot API: http://${HOST}:${PORT}`);
    console.log('Login: POST /api/auth/login com credenciais configuradas no backend.');
});

server.on('error', (error) => {
    console.error('Falha ao iniciar Dashboard API:', error.message);
    process.exit(1);
});

server.on('upgrade', (req, socket, head) => {
    if (!req.url.startsWith('/hubs/notifications')) {
        socket.destroy();
        return;
    }

    const target = new URL(BACKEND_URL);
    const upstream = net.connect(Number(target.port || 80), target.hostname, () => {
        upstream.write(`${req.method} ${req.url} HTTP/${req.httpVersion}\r\n`);
        for (const [key, value] of Object.entries(req.headers)) {
            upstream.write(`${key}: ${value}\r\n`);
        }
        upstream.write(`host: ${target.host}\r\n\r\n`);
        if (head?.length) upstream.write(head);
        upstream.pipe(socket);
        socket.pipe(upstream);
    });

    upstream.on('error', () => socket.destroy());
});

function shutdown(signal) {
    console.log(`${signal} recebido. Encerrando Dashboard API...`);
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGINT', () => shutdown('SIGINT'));
process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('unhandledRejection', (reason) => {
    console.error('Unhandled rejection no Dashboard API:', reason);
});
process.on('uncaughtException', (error) => {
    console.error('Uncaught exception no Dashboard API:', error);
    process.exit(1);
});


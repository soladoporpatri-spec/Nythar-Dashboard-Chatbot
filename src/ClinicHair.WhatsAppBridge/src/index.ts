import http from 'node:http';
import path from 'node:path';
import { BaileysBridgeManager } from './baileysManager.js';

const PORT = Number(process.env.BRIDGE_PORT || 3100);
const WEBHOOK_URL = process.env.CSHARP_WEBHOOK_URL || 'http://127.0.0.1:5150/api/whatsapp/webhook';
const SHARED_SECRET = process.env.BRIDGE_SHARED_SECRET || 'ClinicHair@BridgeHmacSecret2026!';
const STORAGE_DIR = path.resolve(process.cwd(), 'storage');

// Rede de segurança: nenhum erro inesperado derruba o processo (a API também supervisiona e religa o bridge).
process.on('uncaughtException', err => {
  console.error('[ClinicHair.WhatsAppBridge] uncaughtException (processo mantido):', err);
});
process.on('unhandledRejection', reason => {
  console.error('[ClinicHair.WhatsAppBridge] unhandledRejection (processo mantido):', reason);
});

const bridge = new BaileysBridgeManager(STORAGE_DIR, WEBHOOK_URL, SHARED_SECRET);

/**
 * Cache de idempotência para envios (Idempotency-Key). Se a API repetir a mesma requisição
 * (timeout/retry), o bridge devolve o resultado original em vez de enviar a mensagem duas vezes.
 * Requisições concorrentes com a mesma chave compartilham a mesma promessa (single-flight).
 */
const IDEMPOTENCY_TTL_MS = 10 * 60 * 1000;
const idempotencyStore = new Map<string, { expiresAt: number; promise: Promise<unknown> }>();

function runIdempotent<T>(key: string | undefined, work: () => Promise<T>): Promise<T> {
  if (!key) return work();

  const now = Date.now();
  for (const [k, v] of idempotencyStore) {
    if (v.expiresAt < now) idempotencyStore.delete(k);
  }

  const existing = idempotencyStore.get(key);
  if (existing) return existing.promise as Promise<T>;

  const promise = work().catch(err => {
    idempotencyStore.delete(key); // falha não fica cacheada: permite nova tentativa
    throw err;
  });
  idempotencyStore.set(key, { expiresAt: now + IDEMPOTENCY_TTL_MS, promise });
  return promise;
}

function readJsonBody<T>(req: http.IncomingMessage): Promise<T> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', chunk => chunks.push(Buffer.from(chunk)));
    req.on('end', () => {
      try {
        const raw = Buffer.concat(chunks).toString('utf-8');
        resolve((raw ? JSON.parse(raw) : {}) as T);
      } catch (err) {
        reject(err);
      }
    });
    req.on('error', reject);
  });
}

function sendJson(res: http.ServerResponse, statusCode: number, data: unknown): void {
  const payload = JSON.stringify(data);
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(payload)
  });
  res.end(payload);
}

const server = http.createServer(async (req, res) => {
  // Health check público (somente loopback): usado pelo supervisor da API e pelo watchdog.
  if (req.method === 'GET' && req.url === '/health') {
    sendJson(res, 200, bridge.getHealth());
    return;
  }

  const secret = req.headers['x-bridge-secret'];
  if (secret !== SHARED_SECRET) {
    sendJson(res, 401, { error: 'Unauthorized X-Bridge-Secret' });
    return;
  }

  const url = req.url || '/';
  const method = req.method || 'GET';

  try {
    if (method === 'GET' && url === '/bridge/session/status') {
      sendJson(res, 200, bridge.getStatus());
      return;
    }

    if (method === 'POST' && url === '/bridge/session/start') {
      const body = await readJsonBody<{ forceFreshQr?: boolean }>(req).catch(() => ({ forceFreshQr: true }));
      const st = await bridge.startSession(body.forceFreshQr ?? true);
      sendJson(res, 200, st);
      return;
    }

    if (method === 'POST' && url === '/bridge/session/pairing-code') {
      const body = await readJsonBody<{ phoneNumber: string }>(req);
      const st = await bridge.requestPairingCodeForNumber(body.phoneNumber || '');
      sendJson(res, 200, st);
      return;
    }

    if (method === 'POST' && url === '/bridge/session/logout') {
      await bridge.logoutSession();
      sendJson(res, 200, bridge.getStatus());
      return;
    }

    if (method === 'POST' && url === '/bridge/messages/poll') {
      const body = await readJsonBody<{
        recipientJid: string;
        question: string;
        options: string[];
        selectableCount?: number;
      }>(req);

      const idemKey = req.headers['idempotency-key'] as string | undefined;
      const result = await runIdempotent(idemKey, () =>
        bridge.sendPoll(body.recipientJid, body.question, body.options || [], body.selectableCount ?? 1)
      );
      sendJson(res, 200, result);
      return;
    }

    if (method === 'POST' && url === '/bridge/messages/text') {
      const body = await readJsonBody<{
        recipientJid: string;
        text: string;
      }>(req);

      const idemKey = req.headers['idempotency-key'] as string | undefined;
      const result = await runIdempotent(idemKey, () => bridge.sendText(body.recipientJid, body.text));
      sendJson(res, 200, result);
      return;
    }

    sendJson(res, 404, { error: 'Not Found' });
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Internal Bridge Error';
    sendJson(res, 500, { error: message });
  }
});

server.on('error', (err: NodeJS.ErrnoException) => {
  if (err.code === 'EADDRINUSE') {
    console.error(`[ClinicHair.WhatsAppBridge] Porta ${PORT} já está em uso (outra instância ativa). Encerrando esta cópia.`);
    process.exit(0); // saída limpa: a instância existente continua atendendo
  }
  console.error('[ClinicHair.WhatsAppBridge] Erro no servidor HTTP:', err);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`[ClinicHair.WhatsAppBridge] Baileys TypeScript Bridge ativo em http://127.0.0.1:${PORT}`);
});


function gracefulShutdown(signal: string): void {
  console.log(`[ClinicHair.WhatsAppBridge] ${signal} recebido. Encerrando com segurança...`);
  bridge.shutdown();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGINT', () => gracefulShutdown('SIGINT'));
process.on('SIGTERM', () => gracefulShutdown('SIGTERM'));

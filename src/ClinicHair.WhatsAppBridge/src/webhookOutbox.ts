import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

interface SpoolEntry {
  eventId: string;
  body: string;
  attempts: number;
  firstQueuedAt: number;
}

const MAX_ATTEMPTS = 40;
const MAX_AGE_MS = 24 * 60 * 60 * 1000;
const FLUSH_INTERVAL_MS = 8000;
const REQUEST_TIMEOUT_MS = 6000;

/**
 * Outbox durável de webhooks Bridge → API C#.
 * - Tenta entregar imediatamente (timeout curto).
 * - Em falha de rede/5xx, persiste em disco (spool) e reenvia com intervalo até a API responder.
 * - A API é idempotente por EventId, então reentregas nunca duplicam processamento.
 * - Sobrevive a reinício do processo Node e a quedas/reinícios da API.
 */
export class WebhookOutbox {
  private readonly dir: string;
  private flushing = false;
  private timer: NodeJS.Timeout | null = null;

  constructor(
    storageRoot: string,
    private readonly webhookUrl: string,
    private readonly sharedSecret: string
  ) {
    this.dir = path.join(storageRoot, 'webhook_spool');
    fs.mkdirSync(this.dir, { recursive: true });
  }

  public start(): void {
    if (this.timer) return;
    this.timer = setInterval(() => void this.flush(), FLUSH_INTERVAL_MS);
    this.timer.unref();
    void this.flush();
  }

  public stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  public pendingCount(): number {
    try {
      return fs.readdirSync(this.dir).filter(f => f.endsWith('.json')).length;
    } catch {
      return 0;
    }
  }

  /** Eventos efêmeros (QR/pairing) não valem ser reenviados depois de expirar. */
  public async dispatch(payload: Record<string, unknown>, durable = true): Promise<void> {
    const body = JSON.stringify(payload);
    const eventId = String(payload.eventId ?? crypto.randomUUID());
    const result = await this.tryPost(body);

    if (result === 'retry' && durable) {
      this.persist({ eventId, body, attempts: 1, firstQueuedAt: Date.now() });
    }
  }

  private async tryPost(body: string): Promise<'ok' | 'drop' | 'retry'> {
    const signature = crypto.createHmac('sha256', this.sharedSecret).update(body, 'utf8').digest('hex');
    try {
      const res = await fetch(this.webhookUrl, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Bridge-Secret': this.sharedSecret,
          'X-Webhook-Signature': signature
        },
        body,
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS)
      });
      if (res.ok) return 'ok';
      // 4xx = payload/credencial inválidos: reenviar não adianta
      if (res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429) return 'drop';
      return 'retry';
    } catch {
      return 'retry';
    }
  }

  /** Prefixo com timestamp preserva a ordem cronológica de entrega (FIFO). */
  private fileFor(entry: SpoolEntry): string {
    const safe = crypto.createHash('sha256').update(entry.eventId).digest('hex').slice(0, 24);
    return path.join(this.dir, `${String(entry.firstQueuedAt).padStart(15, '0')}_${safe}.json`);
  }

  private persist(entry: SpoolEntry): void {
    try {
      const target = this.fileFor(entry);
      const tmp = `${target}.tmp`;
      fs.writeFileSync(tmp, JSON.stringify(entry), 'utf8');
      fs.renameSync(tmp, target); // escrita atômica
    } catch (err) {
      console.error('[WebhookOutbox] Falha ao persistir evento no spool:', err);
    }
  }

  public async flush(): Promise<void> {
    if (this.flushing) return;
    this.flushing = true;
    try {
      const files = fs
        .readdirSync(this.dir)
        .filter(f => f.endsWith('.json'))
        .sort();

      for (const file of files) {
        const full = path.join(this.dir, file);
        let entry: SpoolEntry;
        try {
          entry = JSON.parse(fs.readFileSync(full, 'utf8')) as SpoolEntry;
        } catch {
          fs.rmSync(full, { force: true }); // arquivo corrompido
          continue;
        }

        const result = await this.tryPost(entry.body);
        if (result === 'ok' || result === 'drop') {
          fs.rmSync(full, { force: true });
          continue;
        }

        entry.attempts += 1;
        if (entry.attempts > MAX_ATTEMPTS || Date.now() - entry.firstQueuedAt > MAX_AGE_MS) {
          console.error(`[WebhookOutbox] Descartando evento ${entry.eventId} após ${entry.attempts} tentativas.`);
          fs.rmSync(full, { force: true });
          continue;
        }
        this.persist(entry);
        break; // API ainda fora do ar: preserva a ordem e tenta no próximo ciclo
      }
    } catch (err) {
      console.error('[WebhookOutbox] Erro ao descarregar spool:', err);
    } finally {
      this.flushing = false;
    }
  }
}

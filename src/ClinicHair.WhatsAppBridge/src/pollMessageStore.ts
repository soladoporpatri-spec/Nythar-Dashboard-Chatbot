import fs from 'node:fs';
import path from 'node:path';
import type { proto, WAMessageKey } from '@whiskeysockets/baileys';

export interface StoredPollEntry {
  msg: proto.IWebMessageInfo;
  options: string[];
  question: string;
}

/**
 * Cache local persistente de enquetes enviadas (proto.IWebMessageInfo + opções originais).
 * Essencial para descriptografar votos (pollUpdateMessage) no WhatsApp Multi-Device v7.
 */
export class PollMessageStore {
  private readonly filePath: string;
  private readonly entries = new Map<string, StoredPollEntry>();
  private readonly jidMap = new Map<string, string>(); // Mapeia dígitos do telefone -> remoteJid real do WhatsApp
  private readonly maxEntries = 1000;

  constructor(storageDir: string) {
    fs.mkdirSync(storageDir, { recursive: true });
    this.filePath = path.join(storageDir, 'poll_messages_store.json');
    this.loadFromDisk();
  }

  public rememberRemoteJid(phoneDigits: string, remoteJid: string): void {
    if (!phoneDigits || !remoteJid) return;
    this.jidMap.set(phoneDigits, remoteJid);
  }

  public getRememberedRemoteJid(phoneDigits: string): string | undefined {
    return this.jidMap.get(phoneDigits);
  }

  public savePollMessage(msg: proto.IWebMessageInfo, question = '', options: string[] = []): void {
    const id = msg.key?.id;
    if (!id) return;

    const entry: StoredPollEntry = { msg, question, options };
    this.entries.set(id, entry);
    if (msg.key?.remoteJid) {
      this.entries.set(`${msg.key.remoteJid}:${id}`, entry);
    }

    if (this.entries.size > this.maxEntries) {
      const oldestKey = this.entries.keys().next().value;
      if (oldestKey) {
        this.entries.delete(oldestKey);
      }
    }

    this.flushToDisk();
  }

  public getMessage(key: WAMessageKey): proto.IMessage | undefined {
    const found = this.getFullEntry(key);
    return found?.msg?.message ?? undefined;
  }

  public getFullMessage(key: WAMessageKey): proto.IWebMessageInfo | undefined {
    return this.getFullEntry(key)?.msg;
  }

  public getFullEntry(key: WAMessageKey): StoredPollEntry | undefined {
    if (!key?.id) return undefined;
    if (key.remoteJid) {
      const byComposite = this.entries.get(`${key.remoteJid}:${key.id}`);
      if (byComposite) return byComposite;
    }
    return this.entries.get(key.id);
  }

  private loadFromDisk(): void {
    try {
      if (!fs.existsSync(this.filePath)) return;
      const raw = fs.readFileSync(this.filePath, 'utf-8');
      const parsed = JSON.parse(raw, this.bufferReviver) as Record<string, StoredPollEntry>;
      for (const [k, v] of Object.entries(parsed)) {
        if (v && v.msg) {
          this.entries.set(k, v);
        }
      }
    } catch {
      // Ignora arquivo antigo/corrompido
    }
  }

  private flushToDisk(): void {
    try {
      const obj: Record<string, StoredPollEntry> = {};
      for (const [k, v] of this.entries.entries()) {
        obj[k] = v;
      }
      fs.writeFileSync(this.filePath, JSON.stringify(obj, this.bufferReplacer, 2), 'utf-8');
    } catch {
      // Ignora erro secundário de disco
    }
  }

  private bufferReplacer(_key: string, value: unknown): unknown {
    if (value instanceof Uint8Array || Buffer.isBuffer(value)) {
      return { __type: 'Uint8Array', data: Buffer.from(value).toString('base64') };
    }
    return value;
  }

  private bufferReviver(_key: string, value: unknown): unknown {
    if (
      value &&
      typeof value === 'object' &&
      '__type' in value &&
      (value as { __type: string }).__type === 'Uint8Array' &&
      'data' in value
    ) {
      return new Uint8Array(Buffer.from((value as { data: string }).data, 'base64'));
    }
    return value;
  }
}

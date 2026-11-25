import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import makeWASocket, {
  Browsers,
  decryptPollVote,
  DisconnectReason,
  fetchLatestBaileysVersion,
  getAggregateVotesInPollMessage,
  jidNormalizedUser,
  normalizeMessageContent,
  useMultiFileAuthState,
  type proto,
  type WAMessageKey,
  type WASocket
} from '@whiskeysockets/baileys';
import pino from 'pino';
import QRCode from 'qrcode';
import { PollMessageStore } from './pollMessageStore.js';
import { WebhookOutbox } from './webhookOutbox.js';

export interface BridgeStatusSnapshot {
  status: 'DISCONNECTED' | 'CONNECTING' | 'QR_READY' | 'PAIRING_CODE_READY' | 'CONNECTED';
  connectedPhone: string | null;
  qrCodeBase64: string | null;
  qrCodeRaw: string | null;
  pairingCode: string | null;
  pairingPhone: string | null;
}

/**
 * Gerenciador da conexão Multi-Device com o WhatsApp Web via @whiskeysockets/baileys v7.
 * - Suporta conexão real por QR Code (com reconexão automática após código 515 restartRequired).
 * - Suporta conexão real por Código de Pareamento de 8 dígitos (requestPairingCode).
 * - Descriptografa votos de enquetes diretamente em messages.upsert (compatível com Baileys v7).
 */
export class BaileysBridgeManager {
  private sock: WASocket | null = null;
  private status: BridgeStatusSnapshot['status'] = 'DISCONNECTED';
  private connectedPhone: string | null = null;
  private qrCodeBase64: string | null = null;
  private qrCodeRaw: string | null = null;
  private pairingCode: string | null = null;
  private pairingPhone: string | null = null;
  private readonly authDir: string;
  private readonly pollStore: PollMessageStore;
  private readonly logger = pino({ level: 'silent' });
  private isStarting = false;
  private reconnectTimer: NodeJS.Timeout | null = null;
  private suppressQrBroadcast = false;
  private reconnectAttempt = 0;
  private lastStatusChangeAt = Date.now();
  private watchdogTimer: NodeJS.Timeout | null = null;
  private lastCredsBackupAt = 0;
  private readonly outbox: WebhookOutbox;

  constructor(
    storageRoot: string,
    webhookUrl: string,
    sharedSecret: string
  ) {
    this.authDir = path.join(storageRoot, 'baileys_auth');
    this.pollStore = new PollMessageStore(path.join(storageRoot, 'poll_cache'));
    this.outbox = new WebhookOutbox(storageRoot, webhookUrl, sharedSecret);
    this.outbox.start();

    this.restoreCredsIfCorrupted();

    // Se já houver credenciais registradas no disco de uma sessão anterior, reconecta automaticamente ao subir!
    const credsFile = path.join(this.authDir, 'creds.json');
    if (fs.existsSync(credsFile)) {
      setTimeout(() => {
        void this.safeInitialize(false);
      }, 500);
    }

    this.startWatchdog();
  }

  /** Snapshot de saúde para o endpoint /health e para o supervisor da API. */
  public getHealth(): {
    ok: true;
    status: BridgeStatusSnapshot['status'];
    statusSinceMs: number;
    reconnectAttempt: number;
    pendingWebhooks: number;
    uptimeSec: number;
  } {
    return {
      ok: true,
      status: this.status,
      statusSinceMs: Date.now() - this.lastStatusChangeAt,
      reconnectAttempt: this.reconnectAttempt,
      pendingWebhooks: this.outbox.pendingCount(),
      uptimeSec: Math.round(process.uptime())
    };
  }

  public shutdown(): void {
    if (this.watchdogTimer) clearInterval(this.watchdogTimer);
    this.outbox.stop();
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    try {
      this.sock?.end(undefined);
    } catch {
      // ignora
    }
  }

  private setStatus(next: BridgeStatusSnapshot['status']): void {
    if (this.status !== next) {
      this.status = next;
      this.lastStatusChangeAt = Date.now();
    }
  }

  /** Inicializa o socket sem nunca propagar exceção (evita unhandledRejection derrubar o processo). */
  private async safeInitialize(suppressQr: boolean): Promise<void> {
    try {
      await this.initializeSocket(suppressQr);
    } catch (err) {
      console.error('[BaileysBridge] Falha ao inicializar socket, nova tentativa agendada:', err);
      this.isStarting = false;
      this.scheduleReconnect(suppressQr, undefined);
    }
  }

  private scheduleReconnect(suppressQr: boolean, statusCode: number | undefined): void {
    if (this.reconnectTimer) return;
    let delayMs: number;
    if (statusCode === DisconnectReason.restartRequired) {
      delayMs = 400; // Fluxo normal pós-QR/pareamento
    } else {
      // Backoff exponencial com jitter: 2.5s, 5s, 10s, 20s, 30s (teto)
      const base = Math.min(2500 * 2 ** this.reconnectAttempt, 30000);
      delayMs = base + Math.floor(Math.random() * 800);
      this.reconnectAttempt += 1;
    }
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.isStarting = false;
      void this.safeInitialize(suppressQr);
    }, delayMs);
  }

  /** Auto-cura: se há credenciais salvas mas a conexão ficou parada, religa sozinho. */
  private startWatchdog(): void {
    this.watchdogTimer = setInterval(() => {
      try {
        const hasCreds = fs.existsSync(path.join(this.authDir, 'creds.json'));
        const stuckMs = Date.now() - this.lastStatusChangeAt;
        const idle = !this.reconnectTimer && !this.isStarting;

        if (hasCreds && idle && this.status !== 'CONNECTED' && this.status !== 'QR_READY' && this.status !== 'PAIRING_CODE_READY' && stuckMs > 60000) {
          console.warn(`[BaileysBridge] Watchdog: conexão parada em ${this.status} há ${Math.round(stuckMs / 1000)}s. Religando...`);
          this.lastStatusChangeAt = Date.now();
          void this.safeInitialize(false);
        }

        if (this.status === 'CONNECTED' && Date.now() - this.lastCredsBackupAt > 10 * 60 * 1000) {
          this.backupCreds();
        }
      } catch (err) {
        console.error('[BaileysBridge] Erro no watchdog:', err);
      }
    }, 20000);
    this.watchdogTimer.unref();
  }

  private backupCreds(): void {
    try {
      const creds = path.join(this.authDir, 'creds.json');
      if (!fs.existsSync(creds)) return;
      JSON.parse(fs.readFileSync(creds, 'utf8')); // só faz backup se estiver íntegro
      fs.copyFileSync(creds, path.join(this.authDir, 'creds.json.bak'));
      this.lastCredsBackupAt = Date.now();
    } catch {
      // creds corrompido no momento: mantém o backup anterior
    }
  }

  private restoreCredsIfCorrupted(): void {
    try {
      const creds = path.join(this.authDir, 'creds.json');
      const bak = path.join(this.authDir, 'creds.json.bak');
      if (!fs.existsSync(creds) || !fs.existsSync(bak)) return;
      try {
        JSON.parse(fs.readFileSync(creds, 'utf8'));
      } catch {
        console.warn('[BaileysBridge] creds.json corrompido (queda durante gravação). Restaurando último backup íntegro.');
        fs.copyFileSync(bak, creds);
      }
    } catch (err) {
      console.error('[BaileysBridge] Falha ao verificar/restaurar creds.json:', err);
    }
  }

  public getStatus(): BridgeStatusSnapshot {
    return {
      status: this.status,
      connectedPhone: this.connectedPhone,
      qrCodeBase64: this.qrCodeBase64,
      qrCodeRaw: this.qrCodeRaw,
      pairingCode: this.pairingCode,
      pairingPhone: this.pairingPhone
    };
  }

  private cleanupSocketOnly(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.sock) {
      try {
        this.sock.ev.removeAllListeners('connection.update');
        this.sock.ev.removeAllListeners('creds.update');
        this.sock.ev.removeAllListeners('messages.upsert');
        this.sock.ev.removeAllListeners('messages.update');
        this.sock.end(undefined);
      } catch {
        // Ignora erro ao encerrar socket anterior
      }
      this.sock = null;
    }
  }

  /**
   * Inicia sessão gerando QR Code real e aguardando o evento QR do WhatsApp Web.
   */
  public async startSession(forceFreshQr = true): Promise<BridgeStatusSnapshot> {
    if (this.status === 'CONNECTED' && this.sock) {
      return this.getStatus();
    }

    if (forceFreshQr) {
      this.cleanupSocketOnly();
      fs.rmSync(this.authDir, { recursive: true, force: true });
      this.qrCodeBase64 = null;
      this.qrCodeRaw = null;
      this.pairingCode = null;
      this.pairingPhone = null;
    }

    this.suppressQrBroadcast = false;
    await this.initializeSocket(false);

    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
      if (this.status === 'QR_READY' && this.qrCodeBase64) break;
      if (this.status === 'CONNECTED') break;
      await new Promise(r => setTimeout(r, 200));
    }

    return this.getStatus();
  }

  /**
   * Gera o Código de Pareamento oficial de 8 dígitos do WhatsApp para o número informado.
   */
  public async requestPairingCodeForNumber(rawPhone: string): Promise<BridgeStatusSnapshot> {
    const cleanDigits = this.normalizeBrazilPhoneDigits(rawPhone);
    if (cleanDigits.length < 10) {
      throw new Error('Informe um número de WhatsApp válido com DDD (ex: 5562982854050).');
    }

    this.cleanupSocketOnly();
    fs.rmSync(this.authDir, { recursive: true, force: true });
    this.qrCodeBase64 = null;
    this.qrCodeRaw = null;
    this.pairingCode = null;
    this.pairingPhone = this.formatPhoneDisplay(cleanDigits);
    this.suppressQrBroadcast = true;

    await this.initializeSocket(true);

    // Aguarda o socket emitir o primeiro QR interno (handshake do protocolo Multi-Device) antes de chamar requestPairingCode
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
      if (this.qrCodeRaw || this.status === 'CONNECTED') break;
      await new Promise(r => setTimeout(r, 150));
    }

    if (!this.sock) {
      throw new Error('Socket do WhatsApp não inicializado.');
    }

    // Pequena pausa após o handshake inicial para garantir que o nó IQ do WhatsApp aceite o requestPairingCode
    await new Promise(r => setTimeout(r, 300));

    const rawCode = await this.sock.requestPairingCode(cleanDigits);
    const formattedCode =
      rawCode && rawCode.length === 8
        ? `${rawCode.slice(0, 4)}-${rawCode.slice(4, 8)}`
        : rawCode;

    this.setStatus('PAIRING_CODE_READY');
    this.pairingCode = formattedCode;
    this.qrCodeBase64 = null;

    console.log(`[BaileysBridge] Pairing Code gerado para ${cleanDigits}: ${formattedCode}`);

    await this.dispatchWebhook({
      eventId: this.createEventId('pairing', `${cleanDigits}_${formattedCode}_${Date.now()}`),
      eventType: 'session.pairing_code',
      senderJid: cleanDigits,
      pairingCode: formattedCode,
      connectedPhone: this.pairingPhone
    });

    return this.getStatus();
  }

  private async initializeSocket(suppressQr: boolean): Promise<void> {
    if (this.isStarting) return;
    this.isStarting = true;

    try {
      this.cleanupSocketOnly();
      if (this.status === 'DISCONNECTED') {
        this.setStatus('CONNECTING');
      }
      fs.mkdirSync(this.authDir, { recursive: true });

      const { state, saveCreds } = await useMultiFileAuthState(this.authDir);
      let version: [number, number, number] = [2, 3000, 1043857760];
      try {
        const latest = await fetchLatestBaileysVersion();
        version = latest.version;
      } catch {
        // Usa versão estável
      }

      const sock = makeWASocket({
        version,
        logger: this.logger,
        printQRInTerminal: false,
        auth: state,
        browser: Browsers.ubuntu('Chrome'),
        syncFullHistory: false,
        markOnlineOnConnect: true,
        getMessage: async key => this.pollStore.getMessage(key)
      });

      this.sock = sock;

      sock.ev.on('creds.update', saveCreds);

      sock.ev.on('connection.update', async update => {
        const { connection, lastDisconnect, qr } = update;

        if (qr) {
          this.qrCodeRaw = qr;
          if (!suppressQr && !this.suppressQrBroadcast && this.status !== 'PAIRING_CODE_READY') {
            this.setStatus('QR_READY');
            this.qrCodeBase64 = await QRCode.toDataURL(qr, { margin: 1, width: 280 });
            console.log('[BaileysBridge] Novo QR Code gerado e pronto para leitura.');

            await this.dispatchWebhook({
              eventId: this.createEventId('qr', qr),
              eventType: 'session.qr',
              senderJid: 'system',
              qrCodeBase64: this.qrCodeBase64,
              qrCodeRaw: this.qrCodeRaw
            });
          }
        }

        if (connection === 'open') {
          this.setStatus('CONNECTED');
          this.reconnectAttempt = 0;
          this.backupCreds();
          this.qrCodeBase64 = null;
          this.qrCodeRaw = null;
          this.pairingCode = null;
          this.suppressQrBroadcast = false;

          const meId = sock.user?.id ? jidNormalizedUser(sock.user.id) : '';
          const rawDigits = meId ? meId.split('@')[0].split(':')[0].replace(/\D/g, '') : '';
          this.connectedPhone = rawDigits ? this.formatPhoneDisplay(rawDigits) : (this.pairingPhone || 'Conectado');

          console.log(`[BaileysBridge] WhatsApp CONECTADO com sucesso! Número: ${this.connectedPhone}`);

          await this.dispatchWebhook({
            eventId: this.createEventId('status_open', `${Date.now()}`),
            eventType: 'session.status',
            senderJid: meId || 'system',
            sessionStatus: 'CONNECTED',
            connectedPhone: this.connectedPhone
          });
        }

        if (connection === 'close') {
          const statusCode = (lastDisconnect?.error as { output?: { statusCode?: number } })?.output?.statusCode;
          const isLoggedOut = statusCode === DisconnectReason.loggedOut;

          console.log(`[BaileysBridge] Conexão fechada (statusCode=${statusCode}, isLoggedOut=${isLoggedOut}, status=${this.status})`);

          if (!isLoggedOut) {
            // IMPORTANTE: Quando o usuário lê o QR Code ou digita os 8 dígitos no celular,
            // o WhatsApp fecha o socket inicial com código 515 (restartRequired) para reconectar autenticado!
            // Para quedas reais usa backoff exponencial com jitter (auto-religação sem martelar o WhatsApp).
            if (this.status === 'CONNECTED') {
              this.setStatus('CONNECTING');
            }
            this.scheduleReconnect(this.suppressQrBroadcast, statusCode);
          } else {
            this.setStatus('DISCONNECTED');
            this.connectedPhone = null;
            this.qrCodeBase64 = null;
            this.qrCodeRaw = null;
            this.pairingCode = null;
            this.pairingPhone = null;
            fs.rmSync(this.authDir, { recursive: true, force: true });

            await this.dispatchWebhook({
              eventId: this.createEventId('status_close', `${Date.now()}`),
              eventType: 'session.status',
              senderJid: 'system',
              sessionStatus: 'DISCONNECTED'
            });
          }
        }
      });

      // Captura tanto mensagens de texto quanto votos em enquetes (pollUpdateMessage no Baileys v7)
      sock.ev.on('messages.upsert', async ({ messages, type }) => {
        if (type !== 'notify' && type !== 'append') return;

        for (const msg of messages) {
          if (!msg.message) continue;

          const remoteJid = msg.key.remoteJid;
          if (!remoteJid || remoteJid === 'status@broadcast' || remoteJid.endsWith('@g.us')) {
            continue;
          }

          // Resolve o JID real com número de telefone (caso o WhatsApp envie remoteJidAlt para @lid)
          const keyAny = msg.key as WAMessageKey & { remoteJidAlt?: string; participantAlt?: string };
          const effectiveSenderJid =
            (remoteJid.endsWith('@lid') && keyAny.remoteJidAlt)
              ? keyAny.remoteJidAlt
              : remoteJid;

          const senderDigits = effectiveSenderJid.split('@')[0].split(':')[0].replace(/\D/g, '');
          if (senderDigits) {
            this.pollStore.rememberRemoteJid(senderDigits, remoteJid);
          }

          const content = normalizeMessageContent(msg.message);
          if (!content) continue;

          // 1. Verifica se é um voto em uma Enquete Nativa (pollUpdateMessage)
          if (content.pollUpdateMessage) {
            await this.handleIncomingPollUpdateMessage(sock, msg, content.pollUpdateMessage, effectiveSenderJid);
            continue;
          }

          // 2. Ignora mensagens de texto enviadas pelo próprio bot
          if (msg.key.fromMe) continue;

          const text =
            content.conversation ||
            content.extendedTextMessage?.text ||
            '';

          if (text.trim().length > 0) {
            console.log(`[BaileysBridge] Mensagem recebida de ${effectiveSenderJid}: "${text.trim()}"`);
            await this.dispatchWebhook({
              eventId: this.createEventId('msg_text', `${msg.key.id}_${effectiveSenderJid}`),
              eventType: 'message.text',
              senderJid: effectiveSenderJid,
              textMessage: text.trim()
            });
          }
        }
      });

      // Fallback adicional caso messages.update seja emitido
      sock.ev.on('messages.update', async updates => {
        for (const { key, update } of updates) {
          if (!update.pollUpdates || update.pollUpdates.length === 0) continue;

          const pollEntry = this.pollStore.getFullEntry(key);
          if (!pollEntry?.msg?.message) continue;

          const meId = sock.user?.id ? jidNormalizedUser(sock.user.id) : undefined;
          const votes = getAggregateVotesInPollMessage(
            {
              message: pollEntry.msg.message,
              pollUpdates: update.pollUpdates
            },
            meId
          );

          const selectedOptions = votes
            .filter(v => v.voters.length > 0)
            .map(v => v.name);

          if (selectedOptions.length === 0) continue;

          const voterJid = key.remoteJid || 'unknown@s.whatsapp.net';
          const pollMsgId = key.id || '';

          await this.dispatchWebhook({
            eventId: this.createEventId('poll_vote', `${pollMsgId}_${voterJid}_${selectedOptions.join('|')}_${Date.now()}`),
            eventType: 'poll.vote',
            senderJid: voterJid,
            pollMessageId: pollMsgId,
            selectedOptions
          });
        }
      });
    } finally {
      this.isStarting = false;
    }
  }

  /**
   * Descriptografa o voto da enquete (pollUpdateMessage) diretamente no Baileys v7.
   */
  private async handleIncomingPollUpdateMessage(
    sock: WASocket,
    msg: proto.IWebMessageInfo,
    pollUpdate: proto.Message.IPollUpdateMessage,
    effectiveSenderJid: string
  ): Promise<void> {
    const creationKey = pollUpdate.pollCreationMessageKey;
    if (!creationKey?.id || !pollUpdate.vote) return;

    const storedEntry = this.pollStore.getFullEntry(creationKey);
    if (!storedEntry?.msg?.message) {
      console.warn(`[BaileysBridge] Enquete original ${creationKey.id} não encontrada no cache local.`);
      return;
    }

    const pollMsg = storedEntry.msg.message;
    const pollEncKey =
      pollMsg.messageContextInfo?.messageSecret ||
      pollMsg.pollCreationMessage?.encKey ||
      pollMsg.pollCreationMessageV2?.encKey ||
      pollMsg.pollCreationMessageV3?.encKey;

    if (!pollEncKey) {
      console.warn(`[BaileysBridge] Chave messageSecret ausente na enquete ${creationKey.id}.`);
      return;
    }

    const mePn = sock.user?.id ? jidNormalizedUser(sock.user.id) : '';
    const meLid = (sock.user as { lid?: string } | undefined)?.lid
      ? jidNormalizedUser((sock.user as { lid?: string }).lid!)
      : '';

    const msgKey = msg.key || {};
    const keyAny = msgKey as WAMessageKey & { remoteJidAlt?: string; participantAlt?: string };

    const creatorCandidates = Array.from(
      new Set(
        [
          mePn,
          meLid,
          creationKey.participant ? jidNormalizedUser(creationKey.participant) : '',
          creationKey.remoteJid ? jidNormalizedUser(creationKey.remoteJid) : ''
        ].filter(Boolean)
      )
    );

    const voterCandidates = Array.from(
      new Set(
        [
          msgKey.remoteJid ? jidNormalizedUser(msgKey.remoteJid) : '',
          keyAny.remoteJidAlt ? jidNormalizedUser(keyAny.remoteJidAlt) : '',
          msgKey.participant ? jidNormalizedUser(msgKey.participant) : '',
          keyAny.participantAlt ? jidNormalizedUser(keyAny.participantAlt) : '',
          effectiveSenderJid ? jidNormalizedUser(effectiveSenderJid) : '',
          mePn,
          meLid
        ].filter(Boolean)
      )
    );

    let decryptedVote: proto.Message.IPollVoteMessage | null = null;

    outer: for (const pollCreatorJid of creatorCandidates) {
      for (const voterJid of voterCandidates) {
        try {
          decryptedVote = decryptPollVote(pollUpdate.vote, {
            pollEncKey,
            pollCreatorJid,
            pollMsgId: creationKey.id,
            voterJid
          });
          if (decryptedVote) break outer;
        } catch {
          // Tenta a próxima combinação de JID (PN vs LID)
        }
      }
    }

    if (!decryptedVote || !decryptedVote.selectedOptions || decryptedVote.selectedOptions.length === 0) {
      return;
    }

    // Mapeia os hashes SHA-256 das opções votadas para os textos originais da enquete
    const pollOptions =
      storedEntry.options.length > 0
        ? storedEntry.options
        : (
            pollMsg.pollCreationMessage?.options ||
            pollMsg.pollCreationMessageV2?.options ||
            pollMsg.pollCreationMessageV3?.options ||
            []
          ).map(o => o.optionName || '');

    const hashToOptionName = new Map<string, string>();
    for (const optName of pollOptions) {
      const shaHex = crypto.createHash('sha256').update(Buffer.from(optName, 'utf8')).digest('hex');
      hashToOptionName.set(shaHex, optName);
    }

    const selectedOptions: string[] = [];
    for (const optHashBytes of decryptedVote.selectedOptions) {
      const hex = Buffer.from(optHashBytes).toString('hex');
      const matchedName = hashToOptionName.get(hex);
      if (matchedName) {
        selectedOptions.push(matchedName);
      }
    }

    if (selectedOptions.length === 0) return;

    console.log(`[BaileysBridge] Voto descriptografado de ${effectiveSenderJid} na enquete ${creationKey.id}: [${selectedOptions.join(', ')}]`);

    await this.dispatchWebhook({
      eventId: this.createEventId(
        'poll_vote',
        `${creationKey.id}_${effectiveSenderJid}_${selectedOptions.join('|')}_${msgKey.id || Date.now()}`
      ),
      eventType: 'poll.vote',
      senderJid: effectiveSenderJid,
      pollMessageId: creationKey.id,
      selectedOptions
    });
  }

  public async logoutSession(): Promise<void> {
    try {
      if (this.sock) {
        await this.sock.logout();
      }
    } catch {
      // Ignora erro caso já desconectado
    } finally {
      this.cleanupSocketOnly();
      this.setStatus('DISCONNECTED');
      this.connectedPhone = null;
      this.qrCodeBase64 = null;
      this.qrCodeRaw = null;
      this.pairingCode = null;
      this.pairingPhone = null;
      fs.rmSync(this.authDir, { recursive: true, force: true });
    }
  }

  /**
   * Simula presença humana ("Digitando...") com jitter aleatório (350ms–700ms) para proteção anti-banimento no WhatsApp.
   */
  private async simulateHumanTyping(jid: string): Promise<void> {
    if (!this.sock || this.status !== 'CONNECTED') return;
    try {
      await this.sock.sendPresenceUpdate('composing', jid);
      const delayMs = 350 + Math.floor(Math.random() * 350);
      await new Promise(resolve => setTimeout(resolve, delayMs));
      await this.sock.sendPresenceUpdate('paused', jid);
    } catch {
      // Nunca interrompe o envio caso o status de presença falhe
    }
  }

  public async sendPoll(
    recipientJid: string,
    question: string,
    options: string[],
    selectableCount = 1
  ): Promise<{ messageId: string; deliveredToBridge: boolean }> {
    const fallbackId = `poll_${crypto.randomUUID().replace(/-/g, '').slice(0, 20)}`;
    if (!this.sock || this.status !== 'CONNECTED') {
      return { messageId: fallbackId, deliveredToBridge: false };
    }

    const jid = await this.resolveTargetJid(recipientJid);
    await this.simulateHumanTyping(jid);
    const cleanOptions = options.slice(0, 12);
    const sent = await this.sock.sendMessage(jid, {
      poll: {
        name: question,
        values: cleanOptions,
        selectableCount
      }
    });

    if (sent) {
      this.pollStore.savePollMessage(sent, question, cleanOptions);
    }

    return {
      messageId: sent?.key?.id || fallbackId,
      deliveredToBridge: true
    };
  }

  public async sendText(
    recipientJid: string,
    text: string
  ): Promise<{ messageId: string; deliveredToBridge: boolean }> {
    const fallbackId = `txt_${crypto.randomUUID().replace(/-/g, '').slice(0, 20)}`;
    if (!this.sock || this.status !== 'CONNECTED') {
      return { messageId: fallbackId, deliveredToBridge: false };
    }

    const jid = await this.resolveTargetJid(recipientJid);
    await this.simulateHumanTyping(jid);
    const sent = await this.sock.sendMessage(jid, { text });

    return {
      messageId: sent?.key?.id || fallbackId,
      deliveredToBridge: true
    };
  }

  /**
   * Resolve o JID exato do destinatário no WhatsApp (tratando o 9º dígito no Brasil, envio para o próprio número conectado e sessões @lid).
   */
  private async resolveTargetJid(raw: string): Promise<string> {
    if (raw.endsWith('@lid')) return raw;
    const digits = this.normalizeBrazilPhoneDigits(raw.split('@')[0].split(':')[0]);

    // 1. Verifica se o destinatário é o próprio aparelho conectado (ex: funcionário/dono recebendo aviso no seu WhatsApp)
    if (this.sock?.user?.id) {
      const meJid = jidNormalizedUser(this.sock.user.id);
      const meDigits = meJid.split('@')[0].split(':')[0].replace(/\D/g, '');
      if (this.areBrazilPhoneDigitsEquivalent(digits, meDigits)) {
        return meJid;
      }
    }

    // 2. Verifica cache de conversas recentes
    const remembered = this.pollStore.getRememberedRemoteJid(digits);
    if (remembered) return remembered;

    const altDigits = this.getBrazilAlternativeNinthDigit(digits);
    if (altDigits) {
      const rememberedAlt = this.pollStore.getRememberedRemoteJid(altDigits);
      if (rememberedAlt) return rememberedAlt;
    }

    // 3. Consulta no WhatsApp Web ambos os formatos (com e sem 9º dígito após o DDD)
    if (this.sock) {
      try {
        const candidates = altDigits ? [digits, altDigits] : [digits];
        const results = await this.sock.onWhatsApp(...candidates);
        const first = results?.find(r => r.exists && r.jid);
        if (first?.jid) {
          this.pollStore.rememberRemoteJid(digits, first.jid);
          return first.jid;
        }
      } catch {
        // Fallback para o JID padrão
      }
    }

    return `${digits}@s.whatsapp.net`;
  }

  private getBrazilAlternativeNinthDigit(digits: string): string | null {
    if (digits.startsWith('55') && digits.length === 13 && digits[4] === '9') {
      return `${digits.slice(0, 4)}${digits.slice(5)}`;
    }
    if (digits.startsWith('55') && digits.length === 12) {
      return `${digits.slice(0, 4)}9${digits.slice(4)}`;
    }
    return null;
  }

  private areBrazilPhoneDigitsEquivalent(a: string, b: string): boolean {
    if (!a || !b) return false;
    if (a === b) return true;
    const altA = this.getBrazilAlternativeNinthDigit(a);
    return altA === b;
  }

  private normalizeBrazilPhoneDigits(raw: string): string {
    const digits = raw.replace(/\D/g, '');
    if (digits.length === 10 || digits.length === 11) {
      return `55${digits}`;
    }
    return digits;
  }

  private formatPhoneDisplay(digits: string): string {
    if (digits.startsWith('55') && digits.length === 13) {
      return `+55 (${digits.slice(2, 4)}) ${digits.slice(4, 9)}-${digits.slice(9)}`;
    }
    if (digits.startsWith('55') && digits.length === 12) {
      return `+55 (${digits.slice(2, 4)}) ${digits.slice(4, 8)}-${digits.slice(8)}`;
    }
    return `+${digits}`;
  }

  private createEventId(prefix: string, seed: string): string {
    const hash = crypto.createHash('sha256').update(`${prefix}:${seed}`).digest('hex').slice(0, 32);
    return `${prefix}_${hash}`;
  }

  private async dispatchWebhook(payload: Record<string, unknown>): Promise<void> {
    // QR/pairing expiram em segundos: só vale tentar na hora. Demais eventos vão para o spool durável.
    const type = String(payload.eventType ?? '');
    const durable = type !== 'session.qr' && type !== 'session.pairing_code';
    try {
      await this.outbox.dispatch(payload, durable);
    } catch (err) {
      console.error('[BaileysBridge] Falha ao despachar webhook:', err);
    }
  }
}

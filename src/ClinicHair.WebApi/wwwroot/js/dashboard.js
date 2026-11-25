(() => {
  'use strict';

  const state = {
    token: localStorage.getItem('ch_token') || sessionStorage.getItem('ch_token') || null,
    username: localStorage.getItem('ch_user') || sessionStorage.getItem('ch_user') || 'Itamar',
    storeName: localStorage.getItem('ch_store') || sessionStorage.getItem('ch_store') || 'Clinic Hair',
    role: localStorage.getItem('ch_role') || sessionStorage.getItem('ch_role') || 'Admin',
    storeId: Number(localStorage.getItem('ch_store_id') || sessionStorage.getItem('ch_store_id') || 1),
    targetStoreId: null,
    impersonatingStoreName: null,
    superAdminOverview: null,
    calYear: new Date().getFullYear(),
    calMonth: new Date().getMonth() + 1,
    barbers: [],
    services: [],
    evaluations: [],
    evalFilter: 'all',
    soundEnabled: localStorage.getItem('ch_sound_enabled') !== 'false',
    unreadNotifications: 0,
    lastSeenNotifId: 0,
    notificationHistory: [],
    queuedToastsWhileModalOpen: []
  };

  const monthNamesPt = [
    'Janeiro', 'Fevereiro', 'Março', 'Abril', 'Maio', 'Junho',
    'Julho', 'Agosto', 'Setembro', 'Outubro', 'Novembro', 'Dezembro'
  ];

  /**
   * Helper seguro para renderizar ícones SVG registrados no sistema ClinicIcons
   * @param {string} name 
   * @param {string} [extraClass=''] 
   * @param {number|string|null} [size=null] 
   * @returns {string} Markup SVG
   */
  function icon(name, extraClass = '', size = null) {
    if (typeof window !== 'undefined' && typeof window.renderIcon === 'function') {
      return window.renderIcon(name, extraClass, size);
    }
    return '';
  }

  function formatCurrencyBRL(value) {
    return new Intl.NumberFormat('pt-BR', {
      style: 'currency',
      currency: 'BRL'
    }).format(Number(value || 0));
  }

  function formatDateBR(isoDate) {
    if (!isoDate || !isoDate.includes('-')) return isoDate || '';
    const [y, m, d] = isoDate.split('-');
    return `${d}/${m}/${y}`;
  }

  function escapeHtml(str) {
    const div = document.createElement('div');
    div.textContent = str ?? '';
    return div.innerHTML;
  }

  function showToast(msg) {
    const toast = document.getElementById('toast-banner');
    if (!toast) return;
    toast.textContent = msg;
    toast.hidden = false;
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => {
      toast.hidden = true;
    }, 3200);
  }

  function buildIdempotencyKey(method, url, body) {
    const raw = `${method}|${url}|${typeof body === 'string' ? body : ''}`;
    let h1 = 0x811c9dc5;
    let h2 = 5381;
    for (let i = 0; i < raw.length; i++) {
      const c = raw.charCodeAt(i);
      h1 = Math.imul(h1 ^ c, 16777619) >>> 0;
      h2 = ((h2 * 33) ^ c) >>> 0;
    }
    return `${h1.toString(16)}-${h2.toString(16)}-${raw.length}`;
  }

  async function apiFetch(url, options = {}) {
    const headers = new Headers(options.headers || {});
    if (state.token) {
      headers.set('Authorization', `Bearer ${state.token}`);
    }
    const activeTargetStore = state.targetStoreId || state.storeId;
    if (activeTargetStore) {
      headers.set('X-Target-Store-Id', String(activeTargetStore));
    }
    if (options.body && !(options.body instanceof FormData)) {
      headers.set('Content-Type', 'application/json');
    }

    // Idempotência: ação mutável idêntica (mesmo método + rota + corpo) repetida em instantes (duplo clique,
    // retry de rede) recebe a resposta original do servidor em vez de executar duas vezes.
    const method = (options.method || 'GET').toUpperCase();
    if (method !== 'GET' && !headers.has('Idempotency-Key')) {
      headers.set('Idempotency-Key', buildIdempotencyKey(method, url, options.body));
    }

    const res = await fetch(url, {
      ...options,
      headers,
      credentials: 'same-origin'
    });

    if (res.status === 401) {
      if (url === '/api/auth/login') {
        let p = {};
        try { p = await res.json(); } catch { /* sem corpo */ }
        throw new Error(p.title === 'TOTP_REQUIRED' ? 'TOTP_REQUIRED' : 'Usuário ou senha inválidos.');
      }
      handleLogout();
      throw new Error('Sessão expirada. Faça login novamente.');
    }

    if (!res.ok) {
      let errText = `Erro ${res.status}`;
      try {
        const problem = await res.json();
        if (res.status === 403 && problem.title === 'PASSWORD_CHANGE_REQUIRED' && !options._pwdRetry) {
          if (await forcePasswordChange()) return apiFetch(url, { ...options, _pwdRetry: true });
        }
        errText = problem.detail || problem.title || errText;
      } catch {
        // Ignora erro de parse
      }
      throw new Error(errText);
    }

    if (res.status === 204) return null;
    return res.json();
  }

  // Troca de senha obrigatória (senha provisória ou de fábrica). Mantido simples até a tela própria.
  async function forcePasswordChange(knownCurrent) {
    const current = knownCurrent || window.prompt('Por segurança, defina uma nova senha.\n\nDigite a senha ATUAL:');
    if (!current) return false;
    const next = window.prompt('Nova senha (mínimo 8 caracteres, com letras e números):');
    if (!next) return false;
    if (window.prompt('Repita a nova senha:') !== next) { showToast('As senhas não conferem.'); return false; }
    const res = await fetch('/api/auth/change-password', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${state.token}` },
      credentials: 'same-origin',
      body: JSON.stringify({ currentPassword: current, newPassword: next })
    });
    if (!res.ok) {
      let msg = 'Não foi possível trocar a senha.';
      try { const p = await res.json(); msg = p.detail || msg; } catch { /* sem corpo */ }
      showToast(msg);
      return false;
    }
    const auth = await res.json();
    state.token = auth.token;
    if (localStorage.getItem('ch_token')) localStorage.setItem('ch_token', auth.token);
    if (sessionStorage.getItem('ch_token')) sessionStorage.setItem('ch_token', auth.token);
    showToast('Senha alterada com sucesso.');
    return true;
  }

  // Exportações exigem confirmação da senha (ticket de 5 minutos)
  document.addEventListener('click', async (e) => {
    const link = e.target.closest('a[href^="/api/dashboard/reports/export/"]');
    if (!link) return;
    e.preventDefault();
    const pwd = window.prompt('Confirme sua senha para exportar os dados:');
    if (!pwd) return;
    try {
      const r = await apiFetch('/api/auth/reauth', { method: 'POST', body: JSON.stringify({ password: pwd }) });
      const [base, qs] = link.getAttribute('href').split('?');
      const params = new URLSearchParams(qs || '');
      params.set('ticket', r.ticket);
      window.location.href = `${base}?${params.toString()}`;
    } catch (err) { showToast(err.message); }
  });

  /* ==========================================================================
     MODAIS <dialog> NATIVOS E FALLBACK (MODERN WEB GUIDANCE: dialog)
     ========================================================================== */
  function setupDialogs() {
    document.querySelectorAll('[data-close-dialog]').forEach(btn => {
      btn.addEventListener('click', () => {
        const dlg = document.getElementById(btn.dataset.closeDialog);
        if (dlg && dlg.open) dlg.close();
      });
    });

    document.querySelectorAll('dialog').forEach(dlg => {
      dlg.addEventListener('close', () => {
        dlg.returnValue = '';
        flushQueuedToastsIfNoModalOpen();
      });
    });

    if (!('closedBy' in HTMLDialogElement.prototype)) {
      const outside = (e, dialog) => {
        const r = dialog.getBoundingClientRect();
        return e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom;
      };
      for (const dialog of document.querySelectorAll('dialog[closedby="any"]')) {
        let pressedOutside = false;
        dialog.addEventListener('pointerdown', e => { pressedOutside = outside(e, dialog); });
        dialog.addEventListener('pointerup', e => {
          if (pressedOutside && outside(e, dialog)) dialog.close();
        });
      }
    }
  }

  /* ==========================================================================
     NAVEGAÇÃO LATERAL VERTICAL (DESKTOP) E SUB-ABAS ISOLADAS NO TOPO
     ========================================================================== */
  function setupNavigation() {
    const navButtons = document.querySelectorAll('.nav-btn[data-section]');
    navButtons.forEach(btn => {
      btn.addEventListener('click', () => {
        const targetSection = btn.dataset.section;
        navButtons.forEach(b => {
          if (b === btn) {
            b.setAttribute('aria-current', 'page');
          } else {
            b.removeAttribute('aria-current');
          }
        });

        document.querySelectorAll('.main-section').forEach(sec => {
          sec.hidden = sec.id !== `section-${targetSection}`;
        });

        loadSectionData(targetSection);
      });
    });

    document.querySelectorAll('.subtab-bar[role="tablist"]').forEach(tablist => {
      const tabs = Array.from(tablist.querySelectorAll('.subtab-btn[role="tab"]'));
      tabs.forEach(tab => {
        tab.addEventListener('click', () => {
          const targetPanelId = tab.dataset.subpanel;
          tabs.forEach(t => {
            const isSelected = t === tab;
            t.setAttribute('aria-selected', isSelected ? 'true' : 'false');
            const panel = document.getElementById(t.dataset.subpanel);
            if (panel) {
              panel.hidden = !isSelected;
            }
          });

          try {
            tab.scrollIntoView({ block: 'nearest', inline: 'nearest', container: 'nearest' });
          } catch {
            tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
          }

          onSubtabActivated(targetPanelId);
        });
      });
    });
  }

  function loadSectionData(section) {
    document.dispatchEvent(new CustomEvent('ch:section', { detail: section }));
    switch (section) {
      case 'superadmin':
        loadSuperAdminOverview();
        break;
      case 'agenda':
        loadAppointments();
        loadCalendarMonth();
        break;
      case 'crm':
        loadCrmCustomers();
        loadAnonymousEvaluations();
        break;
      case 'relatorios':
        loadReports();
        break;
      case 'loja':
        loadStoreBarbers();
        loadStoreServices();
        loadStoreSettings();
        break;
      case 'whatsapp':
        loadWhatsAppStatus();
        break;
    }
  }

  function onSubtabActivated(panelId) {
    if (panelId === 'panel-sa-stores' || panelId === 'panel-sa-financial') loadSuperAdminOverview();
    if (panelId === 'panel-sa-billing') loadSuperAdminBillingOnly();
    if (panelId === 'panel-sa-logs') loadSuperAdminLogsOnly();
    if (panelId === 'panel-agenda-list') loadAppointments();
    if (panelId === 'panel-agenda-calendar') loadCalendarMonth();
    if (panelId === 'panel-crm-customers') loadCrmCustomers();
    if (panelId === 'panel-crm-evaluations') loadAnonymousEvaluations();
    if (panelId === 'panel-loja-barbers') loadStoreBarbers();
    if (panelId === 'panel-loja-services') loadStoreServices();
    if (panelId === 'panel-loja-settings') loadStoreSettings();
  }

  /* ==========================================================================
     SOM DE NOTIFICAÇÃO (WEB AUDIO API) + TOAST POPOVER MANUAL + NOTIFICAÇÃO NO APARELHO
     (MODERN WEB GUIDANCE: persistent-toast-notifications & install-web-app)
     ========================================================================== */
  let audioCtx = null;

  function ensureAudioUnlocked() {
    const AudioContextClass = window.AudioContext || window.webkitAudioContext;
    if (!AudioContextClass) return null;
    if (!audioCtx) {
      audioCtx = new AudioContextClass();
    }
    if (audioCtx.state === 'suspended') {
      audioCtx.resume().catch(() => {});
    }
    return audioCtx;
  }

  function playNotificationChime() {
    if (!state.soundEnabled) return;
    try {
      const ctx = ensureAudioUnlocked();
      if (!ctx) return;

      const now = ctx.currentTime;
      // Nota 1 (D5 - 587.33Hz) e Nota 2 (A5 - 880Hz) estilo sino suave
      const playTone = (freq, startOffset, duration) => {
        const osc = ctx.createOscillator();
        const gain = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.setValueAtTime(freq, now + startOffset);

        gain.gain.setValueAtTime(0.001, now + startOffset);
        gain.gain.exponentialRampToValueAtTime(0.24, now + startOffset + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.001, now + startOffset + duration);

        osc.connect(gain);
        gain.connect(ctx.destination);
        osc.start(now + startOffset);
        osc.stop(now + startOffset + duration + 0.02);
      };

      playTone(587.33, 0, 0.16);
      playTone(880.0, 0.14, 0.28);
    } catch {
      // Ignora caso o navegador bloqueie áudio antes de interação
    }
  }

  function updateSoundToggleUi() {
    const iconEl = document.getElementById('sound-toggle-icon');
    const btnTop = document.getElementById('btn-sound-toggle');
    const btnDlg = document.getElementById('btn-dialog-sound-toggle');

    if (iconEl) iconEl.innerHTML = state.soundEnabled ? icon('volume-2') : icon('volume-x');
    if (btnTop) btnTop.title = state.soundEnabled ? 'Som de Notificações Ativo' : 'Som de Notificações Mudo';
    if (btnDlg) {
      btnDlg.innerHTML = state.soundEnabled
        ? `${icon('volume-2')} Som Ativado`
        : `${icon('volume-x')} Som Desativado`;
      btnDlg.className = state.soundEnabled ? 'btn btn-success' : 'btn btn-secondary';
    }
  }

  function toggleNotificationSound() {
    state.soundEnabled = !state.soundEnabled;
    localStorage.setItem('ch_sound_enabled', String(state.soundEnabled));
    updateSoundToggleUi();
    if (state.soundEnabled) {
      playNotificationChime();
      showToast('Alerta sonoro ativado!');
    } else {
      showToast('Alerta sonoro silenciado.');
    }
  }

  function announceScreenReaderNotification(visibleText) {
    if (typeof document.ariaNotify === 'function') {
      document.ariaNotify(visibleText);
      return;
    }
    const liveEl = document.getElementById('notification-live');
    if (liveEl) {
      liveEl.textContent = '';
      setTimeout(() => {
        liveEl.textContent = visibleText;
      }, 30);
    }
  }

  function isAnyModalDialogOpen() {
    return Boolean(document.querySelector('dialog[open]'));
  }

  function flushQueuedToastsIfNoModalOpen() {
    if (isAnyModalDialogOpen()) return;
    while (state.queuedToastsWhileModalOpen.length > 0) {
      const next = state.queuedToastsWhileModalOpen.shift();
      renderPersistentPopoverToast(next.title, next.summary);
    }
  }

  function showRichNotificationToast(title, summary) {
    // Regra persistent-toast-notifications: não exibe toasts em background enquanto um <dialog> modal estiver aberto
    if (isAnyModalDialogOpen()) {
      state.queuedToastsWhileModalOpen.push({ title, summary });
      return;
    }
    renderPersistentPopoverToast(title, summary);
  }

  function renderPersistentPopoverToast(title, summary) {
    const region = document.getElementById('toast-region');
    if (!region) {
      showToast(`${title}: ${summary}`);
      return;
    }

    const priorFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const toast = document.createElement('div');
    toast.className = 'toast-item';
    toast.setAttribute('popover', 'manual');

    toast.innerHTML = `
      <div class="toast-item-content">
        <p class="toast-item-title">${escapeHtml(title || 'Clinic Hair')}</p>
        <p class="toast-item-body">${escapeHtml(summary)}</p>
      </div>
      <button type="button" class="toast-close-btn" aria-label="Fechar notificação">${icon('x')}</button>
    `;

    region.appendChild(toast);
    try {
      toast.showPopover?.();
    } catch {
      // Fallback caso polyfill ainda esteja inicializando
    }

    announceScreenReaderNotification(`${title ? title + ': ' : ''}${summary}`);

    const dismissToast = (manualFocusMove = false) => {
      clearTimeout(toast._autoTimer);
      const wasFocusedInside = toast.contains(document.activeElement);
      try {
        toast.hidePopover?.();
      } catch {}
      toast.remove();

      if (manualFocusMove && wasFocusedInside) {
        const remainingCloseBtn = region.querySelector('.toast-item .toast-close-btn');
        if (remainingCloseBtn instanceof HTMLElement) {
          remainingCloseBtn.focus();
        } else if (priorFocus && document.contains(priorFocus)) {
          priorFocus.focus();
        }
      }
    };

    const scheduleAutoDismiss = () => {
      clearTimeout(toast._autoTimer);
      toast._autoTimer = setTimeout(() => {
        // Pausa o fechamento automático se o foco estiver dentro do toast
        if (toast.matches(':focus-within')) {
          scheduleAutoDismiss();
          return;
        }
        dismissToast(false);
      }, 6500);
    };

    toast.addEventListener('focusin', () => clearTimeout(toast._autoTimer));
    toast.addEventListener('focusout', scheduleAutoDismiss);
    toast.querySelector('.toast-close-btn')?.addEventListener('click', () => dismissToast(true));

    scheduleAutoDismiss();
  }

  async function dispatchDeviceNotification(title, summary, topic = 'agenda') {
    if (!('Notification' in window)) return;
    if (Notification.permission !== 'granted') return;

    try {
      if ('serviceWorker' in navigator) {
        const reg = await navigator.serviceWorker.ready;
        if (reg && typeof reg.showNotification === 'function') {
          await reg.showNotification(title || 'Clinic Hair', {
            body: summary,
            icon: '/icons/app-192.png',
            badge: '/icons/app-192.png',
            vibrate: [200, 100, 200],
            tag: `clinichair-${Date.now()}`,
            data: { topic }
          });
          return;
        }
      }
      new Notification(title || 'Clinic Hair', {
        body: summary,
        icon: '/icons/app-192.png'
      });
    } catch {
      // Ignora em contextos restritos
    }
  }

  function updateDeviceNotificationPermissionUi() {
    const statusText = document.getElementById('device-notif-status-text');
    const btn = document.getElementById('btn-enable-device-notif');
    if (!statusText || !btn) return;

    if (!('Notification' in window)) {
      statusText.textContent = 'Este navegador não suporta notificações nativas do sistema.';
      btn.disabled = true;
      btn.textContent = 'Indisponível';
      return;
    }

    if (Notification.permission === 'granted') {
      statusText.innerHTML = `${icon('check-circle-2', 'ui-icon-success')} Ativo! Seu aparelho receberá alertas na barra de notificações.`;
      statusText.style.color = '#16a34a';
      btn.innerHTML = `${icon('check')} Ativado no Aparelho`;
      btn.className = 'btn btn-success';
    } else if (Notification.permission === 'denied') {
      statusText.textContent = 'Permissão bloqueada no navegador. Libere nas configurações do site.';
      statusText.style.color = '#dc2626';
      btn.textContent = 'Bloqueado';
    } else {
      statusText.textContent = 'Toque ao lado para permitir alertas na tela do seu celular/computador.';
      statusText.style.color = 'var(--text-secondary)';
      btn.textContent = 'Ativar no Aparelho';
      btn.className = 'btn btn-primary';
    }
  }

  function updateUnreadBadgeUi() {
    const badge = document.getElementById('notif-unread-badge');
    if (!badge) return;
    if (state.unreadNotifications > 0) {
      badge.textContent = state.unreadNotifications > 99 ? '99+' : String(state.unreadNotifications);
      badge.hidden = false;
    } else {
      badge.hidden = true;
    }
  }

  function renderNotificationsHistory() {
    const listEl = document.getElementById('notifications-history-list');
    if (!listEl) return;

    if (!state.notificationHistory || state.notificationHistory.length === 0) {
      listEl.innerHTML = `
        <div style="text-align: center; padding: 20px 12px; color: var(--text-secondary); font-size: 13px;">
          Nenhuma notificação recebida ainda nesta sessão.
        </div>
      `;
      return;
    }

    listEl.innerHTML = state.notificationHistory.map(item => {
      const dt = item.timestamp ? new Date(item.timestamp).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
      return `
        <div style="padding: 10px 12px; border-radius: 9px; background: var(--bg-elevated); border-left: 4px solid var(--brand-primary); display: flex; justify-content: space-between; gap: 10px; align-items: flex-start;">
          <div>
            <strong style="font-size: 12.5px; color: var(--brand-primary); display: block;">${escapeHtml(item.title || 'Clinic Hair')}</strong>
            <span style="font-size: 13px; font-weight: 600; color: var(--text-primary);">${escapeHtml(item.summary)}</span>
          </div>
          <small style="font-size: 11px; color: var(--text-tertiary); white-space: nowrap;">${escapeHtml(dt)}</small>
        </div>
      `;
    }).join('');
  }

  function handleIncomingRealtimeNotification(evt, isInitialSeed = false) {
    if (!evt || !evt.summary) return;
    const numId = Number(evt.id || 0);
    if (numId > 0) {
      if (state.notificationHistory.some(x => Number(x.id) === numId)) {
        return; // Já processado (idempotência entre SSE e Polling)
      }
      if (numId > state.lastSeenNotifId) {
        state.lastSeenNotifId = numId;
      }
    }

    state.notificationHistory.unshift({
      id: numId || Date.now(),
      topic: evt.topic || 'agenda',
      title: evt.title || 'Clinic Hair',
      summary: evt.summary,
      timestamp: evt.timestamp || new Date().toISOString()
    });

    if (state.notificationHistory.length > 35) {
      state.notificationHistory.length = 35;
    }

    renderNotificationsHistory();

    if (isInitialSeed) return;

    state.unreadNotifications += 1;
    updateUnreadBadgeUi();

    // Preferências por tipo (item 83): cada tópico pode ter som e notificação no aparelho desligados
    const prefs = (window.CHPrefs && window.CHPrefs.forTopic(evt.topic || 'agenda')) || { sound: true, device: true, toast: true };
    if (prefs.sound) playNotificationChime();
    if (prefs.toast) showRichNotificationToast(evt.title || 'Clinic Hair', evt.summary);
    if (prefs.device) dispatchDeviceNotification(evt.title || 'Clinic Hair', evt.summary, evt.topic || 'agenda');

    if (evt.topic === 'agenda') {
      loadAppointments();
      loadCalendarMonth();
      loadCrmCustomers();
    } else if (evt.topic === 'evaluations') {
      loadAnonymousEvaluations();
      loadCrmCustomers();
    } else if (evt.topic === 'whatsapp') {
      loadWhatsAppStatus();
    }
  }

  /* ==========================================================================
     AUTENTICAÇÃO (LOGIN ITAMAR / 311208) E STREAM EM TEMPO REAL
     ========================================================================== */
  let realtimeEventSource = null;

  async function syncRecentNotifications(isInitialSeed = false) {
    if (!state.token) return;
    try {
      const afterId = isInitialSeed ? 0 : state.lastSeenNotifId;
      const list = await apiFetch(`/api/dashboard/notifications?afterId=${afterId}`);
      if (Array.isArray(list) && list.length > 0) {
        const ordered = [...list].reverse();
        for (const item of ordered) {
          handleIncomingRealtimeNotification(item, isInitialSeed);
        }
      }
    } catch {
      // Silencioso em background
    }
  }

  function connectRealtimeStream() {
    if (realtimeEventSource) {
      realtimeEventSource.close();
    }
    try {
      const tokenParam = state.token ? `?access_token=${encodeURIComponent(state.token)}` : '';
      realtimeEventSource = new EventSource(`/api/dashboard/realtime/events${tokenParam}`);
      realtimeEventSource.onmessage = event => {
        try {
          const data = JSON.parse(event.data);
          handleIncomingRealtimeNotification(data, false);
        } catch {
          // Ignora pacotes de keep-alive
        }
      };
    } catch {
      // Fallback silencioso via polling
    }
  }

  // Só o perfil SuperAdmin (definido pelo servidor no token) enxerga o painel global.
  function isSuperAdminUser() {
    return state.role === 'SuperAdmin';
  }

  function activateNavSection(sectionName) {
    const navButtons = document.querySelectorAll('.nav-btn[data-section]');
    navButtons.forEach(b => {
      if (b.dataset.section === sectionName) {
        b.setAttribute('aria-current', 'page');
      } else {
        b.removeAttribute('aria-current');
      }
    });

    document.querySelectorAll('.main-section').forEach(sec => {
      sec.hidden = sec.id !== `section-${sectionName}`;
    });

    loadSectionData(sectionName);
  }

  function showAuthenticatedApp() {
    document.getElementById('login-screen').hidden = true;
    document.getElementById('app-shell').hidden = false;

    const superAdmin = isSuperAdminUser();
    const superBtn = document.getElementById('nav-btn-superadmin');
    if (superBtn) {
      superBtn.hidden = !superAdmin;
    }
    // Isolamento do SuperAdmin (item 67): para lojas comuns a área é removida da página, não apenas escondida
    if (!superAdmin) {
      ['section-superadmin', 'nav-btn-superadmin', 'superadmin-impersonate-banner'].forEach(id => document.getElementById(id)?.remove());
      document.querySelectorAll('dialog[id^="dialog-sa-"]').forEach(d => d.remove());
    }
    document.dispatchEvent(new CustomEvent('ch:app-ready', { detail: { superAdmin } }));

    document.getElementById('header-admin-name').innerHTML = superAdmin
      ? `${icon('crown')} ${escapeHtml(state.username)}`
      : `${icon('user')} ${escapeHtml(state.username)}`;
    document.getElementById('header-store-name').textContent = state.storeName;

    updateSoundToggleUi();
    updateDeviceNotificationPermissionUi();

    if (superAdmin && !state.targetStoreId) {
      document.getElementById('superadmin-impersonate-banner').hidden = true;
      activateNavSection('superadmin');
    } else {
      loadStoreBarbers();
      loadStoreServices();
      activateNavSection('agenda');
    }

    syncRecentNotifications(true);
    connectRealtimeStream();
  }

  function handleLogout() {
    if (realtimeEventSource) {
      realtimeEventSource.close();
      realtimeEventSource = null;
    }
    const oldToken = state.token;
    if (oldToken) {
      // Revoga a sessão no servidor (precisa do token antes de apagá-lo)
      fetch('/api/auth/logout', { method: 'POST', headers: { Authorization: `Bearer ${oldToken}` } }).catch(() => {});
    }
    state.token = null;
    state.targetStoreId = null;
    state.impersonatingStoreName = null;
    localStorage.removeItem('ch_token');
    localStorage.removeItem('ch_role');
    sessionStorage.removeItem('ch_token');
    sessionStorage.removeItem('ch_role');
    fetch('/api/auth/logout', { method: 'POST' }).catch(() => {});
    document.getElementById('app-shell').hidden = true;
    document.getElementById('login-screen').hidden = false;
  }

  /* ==========================================================================
     SEÇÃO 0: SUPERADMINISTRADOR (PATRICIO)
     Analisar Lojas, Logs Diretos, Cobrar Assinaturas, Resumo Financeiro e Criar Loja + Pré-Config
     ========================================================================== */
  async function loadSuperAdminOverview() {
    if (!isSuperAdminUser()) return;
    try {
      const [overview, billings, logs] = await Promise.all([
        apiFetch('/api/superadmin/overview'),
        apiFetch('/api/superadmin/billing'),
        apiFetch('/api/superadmin/logs?limit=150')
      ]);
      state.superAdminOverview = overview;
      populateSuperAdminStoreSelectors(overview.stores || []);
      renderSuperAdminStores(overview, billings || []);
      renderSuperAdminFinancial(overview, billings || []);
      renderSuperAdminBilling(billings || []);
      renderSuperAdminLogs(logs || []);
    } catch (err) {
      showToast(err.message);
    }
  }

  function populateSuperAdminStoreSelectors(stores) {
    const chargeSelect = document.getElementById('sa-charge-store');
    const billingFilter = document.getElementById('sa-billing-store-filter');
    const logStoreFilter = document.getElementById('sa-log-store-filter');

    if (chargeSelect) {
      const prev = chargeSelect.value;
      chargeSelect.innerHTML = stores.map(s =>
        `<option value="${s.id}" data-fee="${s.monthlyFee}">${escapeHtml(s.name)} (Admin: ${escapeHtml(s.adminUsername)} • ${formatCurrencyBRL(s.monthlyFee)})</option>`
      ).join('');
      if (prev && stores.some(x => String(x.id) === String(prev))) {
        chargeSelect.value = prev;
      }
    }

    if (billingFilter) {
      const prev = billingFilter.value;
      billingFilter.innerHTML = `<option value="">Todas as Lojas</option>` + stores.map(s =>
        `<option value="${s.id}">${escapeHtml(s.name)}</option>`
      ).join('');
      billingFilter.value = prev;
    }

    if (logStoreFilter) {
      const prev = logStoreFilter.value;
      logStoreFilter.innerHTML = `<option value="">Todas as Lojas</option>` + stores.map(s =>
        `<option value="${s.id}">${escapeHtml(s.name)}</option>`
      ).join('');
      logStoreFilter.value = prev;
    }
  }

  function renderSuperAdminStores(overview, billings = []) {
    const pillsEl = document.getElementById('sa-stores-summary-pills');
    if (pillsEl) {
      pillsEl.innerHTML = `
        <span class="crm-stat-pill">${icon('building-2')} ${overview.totalStores} Loja(s)</span>
        <span class="crm-stat-pill" style="color: #15803d;">${icon('check-circle-2', 'ui-icon-success')} ${overview.activeStores} Ativa(s)</span>
        ${overview.pendingOrOverdueStores > 0 ? `<span class="crm-stat-pill" style="color: #b91c1c;">${icon('clock', 'ui-icon-danger')} ${overview.pendingOrOverdueStores} Pendente/Suspensa</span>` : ''}
        <span class="crm-stat-pill">${icon('message-square')} ${overview.connectedWhatsAppStores} WhatsApp On</span>
      `;
    }

    const grid = document.getElementById('sa-stores-grid');
    if (!grid) return;

    const stores = overview.stores || [];
    if (stores.length === 0) {
      grid.innerHTML = `<div class="surface-card">Nenhuma loja cadastrada.</div>`;
      return;
    }

    grid.innerHTML = stores.map(s => {
      const storeBillings = billings.filter(b => b.storeId === s.id);
      const paidSubTotal = storeBillings.filter(b => b.status === 'Paid').reduce((acc, x) => acc + Number(x.amount || 0), 0);
      const pendSubTotal = storeBillings.filter(b => b.status !== 'Paid').reduce((acc, x) => acc + Number(x.amount || 0), 0);

      const statusBadge = !s.isActive || s.subscriptionStatus === 'Suspended'
        ? `<span class="sa-badge sa-badge-suspended">${icon('ban', 'ui-icon-danger')} Suspensa</span>`
        : s.subscriptionStatus === 'Pending'
          ? `<span class="sa-badge sa-badge-pending">${icon('clock', 'ui-icon-warning')} Cobrança Pendente</span>`
          : `<span class="sa-badge sa-badge-active">${icon('check-circle-2', 'ui-icon-success')} Ativa</span>`;

      const waBadge = s.whatsAppStatus === 'CONNECTED'
        ? `<span class="sa-badge sa-badge-active">${icon('message-square', 'ui-icon-success')} WhatsApp Conectado</span>`
        : `<span class="sa-badge" style="background:#f1f5f9;color:#475569;">${icon('message-square')} ${escapeHtml(s.whatsAppStatus)}</span>`;

      return `
        <article class="sa-store-card" data-store-active="${s.isActive}">
          <div>
            <div class="sa-store-header">
              <div>
                <h3 class="sa-store-title">#${s.id} • ${escapeHtml(s.name)}</h3>
                <p class="sa-store-meta">
                  ${icon('user')} Login Admin: <strong>${escapeHtml(s.adminUsername)}</strong> • ${icon('clock')} ${escapeHtml(s.openingTime)} às ${escapeHtml(s.closingTime)}
                </p>
                <p class="sa-store-meta">
                  ${icon('phone')} Proprietário: <strong>${escapeHtml(s.ownerPhone || s.connectedPhone || 'Não informado')}</strong>
                </p>
              </div>
              <div style="display: flex; flex-direction: column; align-items: flex-end; gap: 4px;">
                ${statusBadge}
                <span class="sa-badge sa-badge-plan">${escapeHtml(s.subscriptionPlan)} • ${formatCurrencyBRL(s.monthlyFee)}/mês</span>
                ${waBadge}
              </div>
            </div>

            <div class="sa-metrics-box" style="margin-top: 12px;">
              <div class="sa-metric-item">
                <small>Faturamento (GMV)</small>
                <strong style="color: var(--brand-primary);">${formatCurrencyBRL(s.confirmedRevenue)}</strong>
              </div>
              <div class="sa-metric-item">
                <small>Concluído</small>
                <strong style="color: #15803d;">${formatCurrencyBRL(s.completedRevenue)}</strong>
              </div>
              <div class="sa-metric-item">
                <small>Ticket Médio</small>
                <strong>${formatCurrencyBRL(s.averageTicket)}</strong>
              </div>
              <div class="sa-metric-item">
                <small>Agendamentos</small>
                <strong>${s.totalAppointments} (${s.completedAppointments} concl.)</strong>
              </div>
              <div class="sa-metric-item">
                <small>Equipe / Serviços</small>
                <strong>${s.barbersCount} barb. • ${s.servicesCount} serv.</strong>
              </div>
              <div class="sa-metric-item">
                <small>Clientes / Aval.</small>
                <strong>${s.customersCount} cli. • ${s.evaluationsCount} aval.</strong>
              </div>
            </div>

            <div style="display: flex; justify-content: space-between; align-items: center; margin-top: 10px; font-size: 12px; color: var(--text-secondary);">
              <span>${icon('calendar')} Próx. Vencimento: <strong>${formatDateBR(s.nextDueDate)}</strong></span>
              <span>${icon('credit-card')} Pago: <strong style="color:#15803d;">${formatCurrencyBRL(paidSubTotal)}</strong> | Pend.: <strong style="color:#b91c1c;">${formatCurrencyBRL(pendSubTotal)}</strong></span>
            </div>
          </div>

          <div class="sa-store-actions">
            <button type="button" class="btn btn-primary sa-btn-impersonate" data-store-id="${s.id}" data-store-name="${escapeHtml(s.name)}">
              ${icon('key')} Acessar Painel da Loja
            </button>
            <button type="button" class="btn btn-secondary sa-btn-preconfig" data-store-id="${s.id}" data-store-name="${escapeHtml(s.name)}">
              ${icon('package')} Entregar Pré-Config
            </button>
            <button type="button" class="btn btn-success sa-btn-charge-store" data-store-id="${s.id}" data-store-fee="${s.monthlyFee}" data-store-due="${escapeHtml(s.nextDueDate)}">
              ${icon('credit-card')} Cobrar Assinatura
            </button>
            <button type="button" class="btn btn-secondary sa-btn-edit-store" data-store-id="${s.id}">
              ${icon('settings')} Editar / Senha / Status
            </button>
          </div>
        </article>
      `;
    }).join('');

    grid.querySelectorAll('.sa-btn-impersonate').forEach(btn => {
      btn.addEventListener('click', () => {
        enterStoreOperationalDashboard(Number(btn.dataset.storeId), btn.dataset.storeName);
      });
    });

    grid.querySelectorAll('.sa-btn-preconfig').forEach(btn => {
      btn.addEventListener('click', () => {
        document.getElementById('sa-preconfig-store-id').value = btn.dataset.storeId;
        document.getElementById('sa-preconfig-store-label').textContent = `Loja selecionada: #${btn.dataset.storeId} — ${btn.dataset.storeName}`;
        document.getElementById('sa-preconfig-barber').value = '';
        document.getElementById('sa-preconfig-barber-phone').value = '';
        document.getElementById('dialog-sa-preconfig').showModal();
      });
    });

    grid.querySelectorAll('.sa-btn-charge-store').forEach(btn => {
      btn.addEventListener('click', () => {
        openSuperAdminChargeModal(Number(btn.dataset.storeId), Number(btn.dataset.storeFee), btn.dataset.storeDue);
      });
    });

    grid.querySelectorAll('.sa-btn-edit-store').forEach(btn => {
      btn.addEventListener('click', () => {
        const st = stores.find(x => x.id === Number(btn.dataset.storeId));
        if (!st) return;
        document.getElementById('sa-edit-store-id').value = st.id;
        document.getElementById('sa-edit-name').value = st.name;
        document.getElementById('sa-edit-owner-phone').value = st.ownerPhone || st.connectedPhone || '';
        document.getElementById('sa-edit-plan').value = st.subscriptionPlan || 'PRO SaaS';
        document.getElementById('sa-edit-fee').value = st.monthlyFee;
        document.getElementById('sa-edit-sub-status').value = st.subscriptionStatus || 'Active';
        document.getElementById('sa-edit-due').value = st.nextDueDate;
        document.getElementById('sa-edit-active').value = st.isActive ? 'true' : 'false';
        document.getElementById('sa-edit-new-pass').value = '';
        document.getElementById('sa-edit-notes').value = st.billingNotes || '';
        document.getElementById('btn-sa-delete-store').hidden = st.id === 1;
        document.getElementById('dialog-sa-edit-store').showModal();
      });
    });
  }

  function renderSuperAdminFinancial(overview, billings = []) {
    const kpiBox = document.getElementById('sa-financial-kpis');
    if (kpiBox) {
      kpiBox.innerHTML = `
        <div class="kpi-card">
          <p class="kpi-label">MRR SaaS (Assinaturas Ativas)</p>
          <p class="kpi-value" style="color: var(--brand-primary);">${formatCurrencyBRL(overview.totalMrrSaaS)}</p>
          <p class="kpi-sub">${overview.activeStores} de ${overview.totalStores} lojas ativas</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Assinaturas Recebidas (Pago)</p>
          <p class="kpi-value" style="color: #15803d;">${formatCurrencyBRL(overview.totalPaidSaaSRevenue)}</p>
          <p class="kpi-sub">Receita SaaS liquidada</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Assinaturas em Aberto</p>
          <p class="kpi-value" style="color: #b91c1c;">${formatCurrencyBRL(overview.totalPendingSaaSRevenue)}</p>
          <p class="kpi-sub">Aguardando confirmação</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">GMV Bruto das Lojas (Agendado)</p>
          <p class="kpi-value">${formatCurrencyBRL(overview.globalConfirmedGmv)}</p>
          <p class="kpi-sub">${overview.globalAppointmentsCount} agendamentos totais</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Receita Concluída nas Lojas</p>
          <p class="kpi-value" style="color: #15803d;">${formatCurrencyBRL(overview.globalCompletedGmv)}</p>
          <p class="kpi-sub">Atendimentos finalizados</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Ticket Médio Global</p>
          <p class="kpi-value">${formatCurrencyBRL(overview.globalAverageTicket)}</p>
          <p class="kpi-sub">Média por atendimento</p>
        </div>
      `;
    }

    const tableBox = document.getElementById('sa-financial-table-container');
    if (!tableBox) return;

    const stores = overview.stores || [];
    tableBox.innerHTML = `
      <table class="sa-table">
        <thead>
          <tr>
            <th>Loja</th>
            <th>Admin</th>
            <th>Plano SaaS</th>
            <th>Mensalidade</th>
            <th>Vencimento</th>
            <th>SaaS Pago</th>
            <th>SaaS Pendente</th>
            <th>Agendamentos</th>
            <th>GMV Loja (Confirmado)</th>
            <th>Concluído Loja</th>
            <th>Ticket Médio</th>
            <th>Ação</th>
          </tr>
        </thead>
        <tbody>
          ${stores.map(s => {
            const storeBillings = billings.filter(b => b.storeId === s.id);
            const paidSub = storeBillings.filter(b => b.status === 'Paid').reduce((acc, x) => acc + Number(x.amount || 0), 0);
            const pendSub = storeBillings.filter(b => b.status !== 'Paid').reduce((acc, x) => acc + Number(x.amount || 0), 0);
            return `
            <tr>
              <td><strong>#${s.id} ${escapeHtml(s.name)}</strong></td>
              <td>${escapeHtml(s.adminUsername)}</td>
              <td><span class="sa-badge sa-badge-plan">${escapeHtml(s.subscriptionPlan)}</span></td>
              <td><strong>${formatCurrencyBRL(s.monthlyFee)}</strong></td>
              <td>${formatDateBR(s.nextDueDate)}</td>
              <td style="color:#15803d;font-weight:700;">${formatCurrencyBRL(paidSub)}</td>
              <td style="color:#b91c1c;font-weight:700;">${formatCurrencyBRL(pendSub)}</td>
              <td>${s.totalAppointments} (${s.completedAppointments} concl.)</td>
              <td><strong>${formatCurrencyBRL(s.confirmedRevenue)}</strong></td>
              <td style="color:#15803d;font-weight:700;">${formatCurrencyBRL(s.completedRevenue)}</td>
              <td>${formatCurrencyBRL(s.averageTicket)}</td>
              <td>
                <button type="button" class="btn btn-primary sa-fin-charge-btn" data-store-id="${s.id}" data-store-fee="${s.monthlyFee}" data-store-due="${escapeHtml(s.nextDueDate)}" style="min-height: 30px; padding: 4px 10px; font-size: 12px;">
                  ${icon('credit-card')} Cobrar
                </button>
              </td>
            </tr>
          `;
          }).join('')}
        </tbody>
      </table>
    `;

    tableBox.querySelectorAll('.sa-fin-charge-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        openSuperAdminChargeModal(Number(btn.dataset.storeId), Number(btn.dataset.storeFee), btn.dataset.storeDue);
      });
    });
  }

  async function loadSuperAdminBillingOnly() {
    try {
      const storeFilterVal = document.getElementById('sa-billing-store-filter')?.value;
      const qs = storeFilterVal ? `?storeId=${encodeURIComponent(storeFilterVal)}` : '';
      const records = await apiFetch(`/api/superadmin/billing${qs}`);
      renderSuperAdminBilling(records || []);
    } catch (err) {
      showToast(err.message);
    }
  }

  function renderSuperAdminBilling(records) {
    const container = document.getElementById('sa-billing-list-container');
    if (!container) return;

    if (!records || records.length === 0) {
      container.innerHTML = `<p style="margin:0;color:var(--text-secondary);font-size:13.5px;">Nenhuma cobrança registrada para este filtro.</p>`;
      return;
    }

    container.innerHTML = `
      <div class="sa-table-scroll">
        <table class="sa-table">
          <thead>
            <tr>
              <th>Fatura</th>
              <th>Loja</th>
              <th>Descrição / Referência</th>
              <th>Valor (R$)</th>
              <th>Vencimento</th>
              <th>Status</th>
              <th>PIX / Link</th>
              <th>Ação</th>
            </tr>
          </thead>
          <tbody>
            ${records.map(r => {
              const isPaid = r.status === 'Paid';
              const statusHtml = isPaid
                ? `<span class="sa-badge sa-badge-active">${icon('check-circle-2', 'ui-icon-success')} Pago</span>`
                : `<span class="sa-badge sa-badge-pending">${icon('clock', 'ui-icon-warning')} Pendente</span>`;
              return `
                <tr>
                  <td>#${r.id}</td>
                  <td><strong>${escapeHtml(r.storeName)}</strong></td>
                  <td>${escapeHtml(r.description)}</td>
                  <td><strong>${formatCurrencyBRL(r.amount)}</strong></td>
                  <td>${formatDateBR(r.dueDate)}</td>
                  <td>${statusHtml}</td>
                  <td style="max-width: 240px; overflow: hidden; text-overflow: ellipsis;">${escapeHtml(r.paymentLinkOrPix || '—')}</td>
                  <td>
                    ${isPaid
                      ? `<span style="font-size:12px;color:#15803d;font-weight:700;">Liquidado</span>`
                      : `<button type="button" class="btn btn-success sa-btn-mark-paid" data-billing-id="${r.id}" style="min-height: 30px; padding: 4px 10px; font-size: 12px;">${icon('check')} Confirmar Pago (+30d)</button>`
                    }
                  </td>
                </tr>
              `;
            }).join('')}
          </tbody>
        </table>
      </div>
    `;

    container.querySelectorAll('.sa-btn-mark-paid').forEach(btn => {
      btn.addEventListener('click', async () => {
        try {
          await apiFetch(`/api/superadmin/billing/${btn.dataset.billingId}/pay`, { method: 'POST' });
          showToast('Pagamento confirmado e assinatura renovada por +30 dias!');
          loadSuperAdminOverview();
        } catch (err) {
          showToast(err.message);
        }
      });
    });
  }

  async function loadSuperAdminLogsOnly() {
    try {
      const level = document.getElementById('sa-log-level-filter')?.value || 'ALL';
      const storeId = document.getElementById('sa-log-store-filter')?.value || '';
      const search = document.getElementById('sa-log-search')?.value || '';
      const params = new URLSearchParams();
      if (level && level !== 'ALL') params.set('level', level);
      if (storeId) params.set('storeId', storeId);
      if (search) params.set('search', search);
      params.set('limit', '180');

      const logs = await apiFetch(`/api/superadmin/logs?${params.toString()}`);
      renderSuperAdminLogs(logs || []);
    } catch (err) {
      showToast(err.message);
    }
  }

  function renderSuperAdminLogs(logs) {
    const term = document.getElementById('sa-logs-terminal');
    if (!term) return;

    if (!logs || logs.length === 0) {
      term.innerHTML = `<div style="color:#94a3b8;text-align:center;padding:30px;">Nenhum registro de log encontrado para os filtros atuais.</div>`;
      return;
    }

    term.innerHTML = logs.map(l => {
      const dt = l.createdAt ? new Date(l.createdAt).toLocaleString('pt-BR') : '';
      return `
        <div class="sa-log-row" data-level="${escapeHtml(l.level)}">
          <div class="sa-log-top">
            <span class="sa-log-time">[${escapeHtml(dt)}]</span>
            <span class="sa-log-level-pill">${escapeHtml(l.level)}</span>
            <span class="sa-log-store-pill">${icon('building-2')} ${escapeHtml(l.storeName || 'Global')}</span>
            <span style="color:#cbd5e1;font-size:11.5px;font-weight:700;">• ${escapeHtml(l.category)}</span>
          </div>
          <div class="sa-log-msg">${escapeHtml(l.message)}</div>
          ${l.details ? `<div class="sa-log-details">${escapeHtml(l.details)}</div>` : ''}
        </div>
      `;
    }).join('');
  }

  function openSuperAdminChargeModal(storeId, fee, dueDate) {
    const now = new Date();
    const refMonth = `${String(now.getMonth() + 1).padStart(2, '0')}/${now.getFullYear()}`;
    const storeSelect = document.getElementById('sa-charge-store');
    if (storeSelect && storeId) {
      storeSelect.value = String(storeId);
    }
    document.getElementById('sa-charge-month').value = refMonth;
    document.getElementById('sa-charge-amount').value = fee || 149.90;
    document.getElementById('sa-charge-due').value = dueDate || new Date(Date.now() + 5 * 86400000).toISOString().slice(0, 10);
    document.getElementById('dialog-sa-charge').showModal();
  }

  async function enterStoreOperationalDashboard(storeId, storeName) {
    // Modo suporte exige motivo registrado no log do SuperAdmin (item 69)
    const reason = window.prompt(`Motivo do acesso ao painel da loja "${storeName}" (fica registrado no log):`);
    if (!reason || reason.trim().length < 5) {
      showToast('Acesso cancelado: informe um motivo com pelo menos 5 caracteres.');
      return;
    }
    try {
      await apiFetch('/api/superadmin/support-session', { method: 'POST', body: JSON.stringify({ storeId, reason: reason.trim() }) });
    } catch (err) {
      showToast(err.message);
      return;
    }
    state.targetStoreId = storeId;
    state.impersonatingStoreName = storeName;
    document.getElementById('header-store-name').textContent = storeName;
    const banner = document.getElementById('superadmin-impersonate-banner');
    const textEl = document.getElementById('sa-impersonate-text');
    if (textEl) {
      textEl.innerHTML = `Modo suporte — você está no painel da loja <strong>#${storeId} • ${escapeHtml(storeName)}</strong>. Todas as alterações ficam registradas.`;
    }
    if (banner) {
      banner.hidden = false;
    }
    showToast(`Acessando painel da loja ${storeName}`);
    activateNavSection('agenda');
  }

  function returnToSuperAdminDashboard() {
    state.targetStoreId = null;
    state.impersonatingStoreName = null;
    document.getElementById('header-store-name').textContent = 'SuperAdmin • Todas as Lojas';
    const banner = document.getElementById('superadmin-impersonate-banner');
    if (banner) banner.hidden = true;
    activateNavSection('superadmin');
  }

  /* ==========================================================================
     SEÇÃO 1A: AGENDA — CARDS RESUMIDOS, COMPACTOS E AFINADOS EM ALTURA
     (Apenas Hora, Nome do Cliente, Profissional, Serviço e Valor)
     ========================================================================== */
  function todayIsoLocal() {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  async function loadAppointments() {
    const filterDateInput = document.getElementById('agenda-filter-date');
    if (!filterDateInput.value) filterDateInput.value = todayIsoLocal();
    const dateParam = `?date=${encodeURIComponent(filterDateInput.value)}`;
    const container = document.getElementById('agenda-cards-container');

    try {
      const all = await apiFetch(`/api/dashboard/appointments${dateParam}`);
      // Só os atendimentos do dia: separados em "Para fazer" e "Feito". Cancelados ficam fora da lista.
      const list = (all || []).filter(a => a.status !== 'Cancelled');
      const cancelledCount = (all || []).length - list.length;
      const todo = list.filter(a => a.status !== 'Completed').sort((x, y) => String(x.startTime).localeCompare(String(y.startTime)));
      const done = list.filter(a => a.status === 'Completed').sort((x, y) => String(x.startTime).localeCompare(String(y.startTime)));

      if (list.length === 0) {
        container.innerHTML = `
          <div class="surface-card" style="text-align: center; padding: 28px 16px;">
            <p style="margin: 0; font-size: 14px; font-weight: 600; color: var(--text-secondary);">
              Nenhum atendimento neste dia.
            </p>
          </div>`;
        return;
      }

      const cardHtml = a => {
        const curPay = a.paymentMethod || 'PIX';
        return `
        <article class="agenda-card" style="--service-color: ${escapeHtml(a.serviceColorHex)};">
          <div class="agenda-card-summary">
            <span class="agenda-time-pill" title="${escapeHtml(formatDateBR(a.appointmentDate))}">
              ${escapeHtml(a.startTime)}
            </span>
            <div class="agenda-summary-strip">
              <span class="agenda-client">${escapeHtml(a.customerName)}</span>
              <span class="agenda-divider">•</span>
              <span class="agenda-barber">${escapeHtml(a.barberName)}</span>
              <span class="agenda-service-price">
                ${escapeHtml(a.serviceName)} • ${formatCurrencyBRL(a.priceCharged)}
              </span>
            </div>
          </div>
          <div class="agenda-card-actions">
            <select
              class="agenda-pay-select"
              data-appt-pay-select="${a.id}"
              data-current-status="${escapeHtml(a.status)}"
              aria-label="Meio de pagamento"
              style="height: 28px; padding: 0 6px; border-radius: 6px; border: 1px solid var(--border-strong); background: var(--bg-surface); font-size: 11.5px; font-weight: 700; color: var(--text-primary); cursor: pointer;">
              <option value="PIX" ${curPay === 'PIX' ? 'selected' : ''}>PIX</option>
              <option value="Cartão" ${curPay === 'Cartão' ? 'selected' : ''}>Cartão</option>
              <option value="Dinheiro" ${curPay === 'Dinheiro' ? 'selected' : ''}>Dinheiro</option>
            </select>
            ${a.status !== 'Completed' ? `
              <button type="button" class="btn-compact btn-success" data-appt-status="${a.id}" data-new-status="Completed">
                Concluir
              </button>` : `<span class="crm-stat-pill" style="padding: 2px 8px; font-size: 11px; background: #ecfdf5; color: #15803d;">${icon('check-circle-2', 'ui-icon-success')} Concluído</span>`}
            ${a.status !== 'Cancelled' && a.status !== 'Completed' ? `
              <button type="button" class="btn-compact btn-danger" data-appt-status="${a.id}" data-new-status="Cancelled">
                Cancelar
              </button>` : ''}
          </div>
        </article>
      `;
      };

      const column = (title, items, emptyText, cls) => `
        <section class="agenda-col ${cls}" aria-label="${escapeHtml(title)}">
          <h3 class="agenda-col-title">${escapeHtml(title)} <span class="agenda-col-count">${items.length}</span></h3>
          <div class="agenda-list">
            ${items.length ? items.map(cardHtml).join('') : `<p class="agenda-col-empty">${escapeHtml(emptyText)}</p>`}
          </div>
        </section>`;

      container.innerHTML = `
        <div class="agenda-split">
          ${column('Para fazer', todo, 'Nenhum atendimento pendente.', 'agenda-col-todo')}
          ${column('Feito', done, 'Nenhum atendimento concluído ainda.', 'agenda-col-done')}
        </div>
        ${cancelledCount > 0 ? `<p class="agenda-col-note">${cancelledCount} cancelado(s) neste dia não aparecem na lista.</p>` : ''}`;

      container.querySelectorAll('[data-appt-pay-select]').forEach(sel => {
        sel.addEventListener('change', async () => {
          const id = sel.dataset.apptPaySelect;
          const status = sel.dataset.currentStatus || 'Confirmed';
          const paymentMethod = sel.value;
          try {
            await apiFetch(`/api/dashboard/appointments/${id}/status`, {
              method: 'PATCH',
              body: JSON.stringify({ status, paymentMethod })
            });
            showToast(`Pagamento atualizado para ${paymentMethod}.`);
          } catch (err) {
            showToast(err.message);
          }
        });
      });

      container.querySelectorAll('[data-appt-status]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const id = btn.dataset.apptStatus;
          const status = btn.dataset.newStatus;
          const paySel = container.querySelector(`[data-appt-pay-select="${id}"]`);
          const paymentMethod = paySel ? paySel.value : 'PIX';
          try {
            await apiFetch(`/api/dashboard/appointments/${id}/status`, {
              method: 'PATCH',
              body: JSON.stringify({ status, paymentMethod })
            });
            showToast(status === 'Completed' ? `Concluído (${paymentMethod})!` : 'Agendamento cancelado.');
            loadAppointments();
          } catch (err) {
            showToast(err.message);
          }
        });
      });
    } catch (err) {
      showToast(err.message);
    }
  }

  async function refreshManualAppointmentSlots() {
    const barberSelect = document.getElementById('appt-barber');
    const dateInput = document.getElementById('appt-date');
    const timeSelect = document.getElementById('appt-time');
    if (!barberSelect.value || !dateInput.value) return;

    try {
      const slots = await apiFetch(
        `/api/dashboard/barbers/${barberSelect.value}/available-slots?date=${encodeURIComponent(dateInput.value)}`
      );
      if (!slots || slots.length === 0) {
        timeSelect.innerHTML = `<option value="">Indisponível</option>`;
        return;
      }
      timeSelect.innerHTML = slots
        .map(s => `<option value="${escapeHtml(s)}">${escapeHtml(s)}</option>`)
        .join('');
    } catch (err) {
      showToast(err.message);
    }
  }

  /* ==========================================================================
     SEÇÃO 1B: CALENDÁRIO (VERMELHO + DIA BRANCO + TOTAL PRETO | AMARELO FOLGA)
     ========================================================================== */
  async function loadCalendarMonth() {
    const label = document.getElementById('calendar-month-label');
    const grid = document.getElementById('calendar-grid-container');
    label.textContent = `${monthNamesPt[state.calMonth - 1]} de ${state.calYear}`;

    try {
      const summary = await apiFetch(
        `/api/dashboard/calendar?year=${state.calYear}&month=${state.calMonth}`
      );

      const firstDayWeekIndex = new Date(state.calYear, state.calMonth - 1, 1).getDay();
      const cellsHtml = [];

      for (let i = 0; i < firstDayWeekIndex; i++) {
        cellsHtml.push(`<div class="calendar-cell empty-cell" aria-hidden="true"></div>`);
      }

      for (const item of summary) {
        const dayNumber = Number(item.date.split('-')[2]);
        let cellClass = 'calendar-cell';
        let badgeHtml = `<span style="font-size: 11px; color: var(--text-tertiary);">Livre</span>`;

        if (item.isNonWorkingDay) {
          cellClass += ' non-working-day';
          badgeHtml = `<span class="calendar-blocked-badge">Não Funcionamento</span>`;
        } else if (item.totalAppointments > 0) {
          cellClass += ' has-appointments';
          badgeHtml = `<span class="calendar-appt-count-badge">${item.totalAppointments} agendamento${item.totalAppointments > 1 ? 's' : ''}</span>`;
        }

        cellsHtml.push(`
          <button type="button" class="${cellClass}" data-cal-date="${escapeHtml(item.date)}" title="Clique para marcar/desmarcar Dia de Não Funcionamento">
            <span class="calendar-day-number">${dayNumber}</span>
            <div>${badgeHtml}</div>
          </button>
        `);
      }

      grid.innerHTML = cellsHtml.join('');

      grid.querySelectorAll('[data-cal-date]').forEach(cell => {
        cell.addEventListener('click', async () => {
          const date = cell.dataset.calDate;
          try {
            const res = await apiFetch('/api/dashboard/calendar/non-working-day', {
              method: 'POST',
              body: JSON.stringify({ date, reason: 'Dia de Não Funcionamento' })
            });
            showToast(
              res.isNonWorkingDay
                ? `Dia ${formatDateBR(date)} marcado como Não Funcionamento.`
                : `Dia ${formatDateBR(date)} liberado.`
            );
            loadCalendarMonth();
          } catch (err) {
            showToast(err.message);
          }
        });
      });
    } catch (err) {
      showToast(err.message);
    }
  }

  /* ==========================================================================
     SEÇÃO 2: CRM (CLIENTES E AVALIAÇÕES COM BOTÕES POSITIVO / NEGATIVO)
     ========================================================================== */
  async function loadCrmCustomers() {
    const container = document.getElementById('crm-customers-container');
    try {
      const list = await apiFetch('/api/dashboard/crm/customers');
      if (!list.length) {
        container.innerHTML = '<div class="ext-empty"><p>Nenhum cliente ainda. Eles aparecem aqui quando agendam pelo WhatsApp ou quando você cria um agendamento manual.</p></div>';
        return;
      }
      container.innerHTML = list.map(c => `
        <article class="crm-card">
          <div>
            <h3 class="crm-customer-title">${escapeHtml(c.name)}</h3>
            <p style="margin: 4px 0 10px; font-size: 13px; color: var(--text-secondary);">
              ${icon('phone')} ${escapeHtml(c.phone)}
            </p>
            <div style="display: flex; gap: 8px; flex-wrap: wrap;">
              <span class="crm-stat-pill">Agendamentos: ${c.totalAppointments}</span>
              <span class="crm-stat-pill">Avaliações: ${c.evaluationCount}</span>
            </div>
          </div>
          <div style="display: flex; gap: 6px; flex-wrap: wrap;">
            <button type="button" class="btn btn-secondary btn-compact" data-profile="${c.id}">Perfil</button>
            <button type="button" class="btn-crm-evaluations" data-customer-evals="${c.id}" data-customer-name="${escapeHtml(c.name)}">
              Avaliações
            </button>
          </div>
        </article>
      `).join('');

      container.querySelectorAll('[data-customer-evals]').forEach(btn => {
        btn.addEventListener('click', () => {
          openCustomerEvaluationsModal(btn.dataset.customerEvals, btn.dataset.customerName);
        });
      });
    } catch (err) {
      showToast(err.message);
    }
  }

  async function openCustomerEvaluationsModal(customerId, customerName) {
    const dlg = document.getElementById('dialog-customer-evals');
    const title = document.getElementById('dialog-evals-title');
    const body = document.getElementById('dialog-customer-evals-body');

    title.textContent = `Avaliações — ${customerName}`;
    body.innerHTML = `<p>Carregando...</p>`;
    dlg.showModal();

    try {
      const evals = await apiFetch(`/api/dashboard/crm/customers/${customerId}/evaluations`);
      if (!evals || evals.length === 0) {
        body.innerHTML = `<p style="color: var(--text-secondary);">Nenhuma avaliação encontrada.</p>`;
        return;
      }

      body.innerHTML = evals.map(ev => {
        const isPos = ev.sentiment === 'Positive';
        const isNeg = ev.sentiment === 'Negative';
        const sentimentClass = isPos ? 'sentiment-positive' : isNeg ? 'sentiment-negative' : '';

        return `
          <div class="eval-card ${sentimentClass}" style="background: var(--bg-elevated);">
            <div style="display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap;">
              <div>
                <strong>${escapeHtml(formatDateBR(ev.appointmentDate))} • ${escapeHtml(ev.serviceName)}</strong>
                <span style="font-size: 12.5px; color: var(--text-secondary);"> (${escapeHtml(ev.barberName)})</span>
              </div>
              <div style="display: flex; align-items: center; gap: 6px;">
                <button type="button" class="btn-eval-positive ${ev.sentiment && !isPos ? 'is-inactive' : ''}" data-modal-sentiment="${ev.id}" data-val="Positive">
                  Positivo
                </button>
                <button type="button" class="btn-eval-negative ${ev.sentiment && !isNeg ? 'is-inactive' : ''}" data-modal-sentiment="${ev.id}" data-val="Negative">
                  Negativo
                </button>
                <a href="/api/dashboard/crm/evaluations/${ev.id}/txt" class="btn btn-secondary" style="min-height: 34px; padding: 4px 10px; font-size: 12px; text-decoration: none;">
                  .txt
                </a>
              </div>
            </div>
            <pre class="eval-txt-pre">${escapeHtml(ev.consolidatedText)}</pre>
          </div>
        `;
      }).join('');

      body.querySelectorAll('[data-modal-sentiment]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const evalId = btn.dataset.modalSentiment;
          const sentiment = btn.dataset.val;
          await setEvaluationSentiment(evalId, sentiment);
          openCustomerEvaluationsModal(customerId, customerName);
        });
      });
    } catch (err) {
      body.innerHTML = `<p style="color: #dc2626;">${escapeHtml(err.message)}</p>`;
    }
  }

  async function setEvaluationSentiment(evalId, sentiment) {
    try {
      await apiFetch(`/api/dashboard/crm/evaluations/${evalId}/sentiment`, {
        method: 'PATCH',
        body: JSON.stringify({ sentiment })
      });
      showToast(sentiment === 'Positive' ? 'Marcada como Positivo!' : 'Marcada como Negativo!');
      await loadAnonymousEvaluations();
    } catch (err) {
      showToast(err.message);
    }
  }

  async function loadAnonymousEvaluations() {
    const statsEl = document.getElementById('storage-stats-text');
    try {
      const [list, stats] = await Promise.all([
        apiFetch('/api/dashboard/crm/evaluations-anonymous'),
        apiFetch('/api/dashboard/crm/evaluations/storage-stats')
      ]);

      state.evaluations = list || [];
      const kb = (stats.totalBytesOnDisk / 1024).toFixed(1);
      statsEl.textContent = `${stats.totalEvaluations} avaliações (${kb} KB em disco)`;

      renderEvaluationsList();
    } catch (err) {
      showToast(err.message);
    }
  }

  function renderEvaluationsList() {
    const container = document.getElementById('anonymous-evaluations-container');
    const filtered = state.evaluations.filter(ev => {
      if (state.evalFilter === 'all') return true;
      return ev.sentiment === state.evalFilter;
    });

    if (filtered.length === 0) {
      container.innerHTML = `<div class="surface-card">Nenhuma avaliação encontrada para este filtro.</div>`;
      return;
    }

    container.innerHTML = filtered.map(ev => {
      const isPos = ev.sentiment === 'Positive';
      const isNeg = ev.sentiment === 'Negative';
      const sentimentClass = isPos ? 'sentiment-positive' : isNeg ? 'sentiment-negative' : '';

      return `
        <article class="eval-card ${sentimentClass}">
          <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 10px;">
            <div style="font-size: 13.5px; color: var(--text-secondary);">
              <strong>${escapeHtml(formatDateBR(ev.appointmentDate))}</strong> •
              Serviço: <strong>${escapeHtml(ev.serviceName)}</strong> •
              Profissional: <strong>${escapeHtml(ev.barberName)}</strong>
            </div>
            <div style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap;">
              <button type="button" class="btn-eval-positive ${ev.sentiment && !isPos ? 'is-inactive' : ''}" data-eval-sentiment="${ev.id}" data-val="Positive">
                Positivo
              </button>
              <button type="button" class="btn-eval-negative ${ev.sentiment && !isNeg ? 'is-inactive' : ''}" data-eval-sentiment="${ev.id}" data-val="Negative">
                Negativo
              </button>
              <a href="/api/dashboard/crm/evaluations/${ev.id}/txt" class="btn btn-secondary" style="min-height: 34px; padding: 4px 10px; font-size: 12px; text-decoration: none;">
                Baixar .txt
              </a>
            </div>
          </div>
          <pre class="eval-txt-pre">${escapeHtml(ev.consolidatedText)}</pre>
        </article>
      `;
    }).join('');

    container.querySelectorAll('[data-eval-sentiment]').forEach(btn => {
      btn.addEventListener('click', () => {
        setEvaluationSentiment(btn.dataset.evalSentiment, btn.dataset.val);
      });
    });
  }

  /* ==========================================================================
     SEÇÃO 3: RELATÓRIOS
     ========================================================================== */
  async function loadReports() {
    const kpiContainer = document.getElementById('reports-kpi-container');
    const barbersContainer = document.getElementById('reports-barbers-container');
    const paymentsContainer = document.getElementById('reports-payments-container');
    const hoursContainer = document.getElementById('reports-hours-container');
    const servicesContainer = document.getElementById('reports-services-container');

    try {
      const r = await apiFetch('/api/dashboard/reports');

      kpiContainer.innerHTML = `
        <div class="kpi-card">
          <p class="kpi-label">Faturamento</p>
          <p class="kpi-value tabular-nums">${formatCurrencyBRL(r.totalConfirmedRevenue)}</p>
          <p class="kpi-sub">${r.totalAppointmentsCount} atendimentos</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Ticket médio</p>
          <p class="kpi-value tabular-nums">${formatCurrencyBRL(r.averageTicket)}</p>
          <p class="kpi-sub">${formatCurrencyBRL(r.totalCompletedRevenue)} já concluído</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Dia mais cheio</p>
          <p class="kpi-value">${escapeHtml(r.busiestDayOfWeek)}</p>
          <p class="kpi-sub">${escapeHtml(formatDateBR(r.busiestDateRecord))} • ${r.busiestDateCount} agend.</p>
        </div>
        <div class="kpi-card">
          <p class="kpi-label">Horário de pico</p>
          <p class="kpi-value">${escapeHtml(r.peakHourSlot)}</p>
          <p class="kpi-sub">${r.peakHourCount} agendamentos</p>
        </div>
      `;

      barbersContainer.innerHTML = r.barberPerformances.map(b => `
        <div class="report-item">
          <div>
            <div class="report-item-title">${escapeHtml(b.barberName)}</div>
            <div class="report-item-sub">${b.totalAppointments} agendamentos • ${b.completedAppointments} concluídos</div>
          </div>
          <div class="report-item-value tabular-nums">${formatCurrencyBRL(b.revenueGenerated)}</div>
        </div>
      `).join('') || '<p class="report-item-sub">Nenhum atendimento no período.</p>';

      if (paymentsContainer) {
        const payList = r.paymentMethodBreakdown || [];
        const maxPayRev = Math.max(1, ...payList.map(p => Number(p.totalRevenue || 0)));
        paymentsContainer.innerHTML = payList.map(p => {
          const pct = Math.round((Number(p.totalRevenue || 0) / maxPayRev) * 100);
          const payIcon = p.paymentMethod === 'PIX' ? icon('zap') : p.paymentMethod === 'Cartão' ? icon('credit-card') : icon('banknote');
          return `
            <div class="report-item" style="flex-direction: column; align-items: stretch; gap: 6px;">
              <div style="display: flex; justify-content: space-between; align-items: center; gap: 8px;">
                <span class="report-item-title" style="display: inline-flex; align-items: center; gap: 6px;">${payIcon} ${escapeHtml(p.paymentMethod)}</span>
                <span class="report-item-value tabular-nums">${formatCurrencyBRL(p.totalRevenue)}</span>
              </div>
              <div class="bar-track" style="height: 6px;">
                <div class="bar-fill" style="width: ${pct}%;"></div>
              </div>
              <div class="report-item-sub" style="margin-top: 0;">${p.count} atendimentos</div>
            </div>
          `;
        }).join('');
      }

      const maxHourCount = Math.max(1, ...r.hourlyDistribution.map(h => h.appointmentCount));
      hoursContainer.innerHTML = r.hourlyDistribution.map(h => {
        const pct = Math.round((h.appointmentCount / maxHourCount) * 100);
        return `
          <div class="bar-row">
            <span class="bar-label">${escapeHtml(h.timeSlot)}</span>
            <div class="bar-track">
              <div class="bar-fill" style="width: ${pct}%;"></div>
            </div>
            <strong style="font-size: 12.5px; min-width: 32px; text-align: right;" class="tabular-nums">${h.appointmentCount}x</strong>
          </div>
        `;
      }).join('');

      servicesContainer.innerHTML = r.serviceBreakdown.map(s => `
        <div style="display: flex; align-items: center; justify-content: space-between; padding: 8px 12px; border-radius: 10px; margin-bottom: 8px; background: linear-gradient(90deg, ${escapeHtml(s.colorHex)} 0%, color-mix(in srgb, ${escapeHtml(s.colorHex)} 22%, #ffffff) 35%, #ffffff 100%); box-shadow: 0 0 0 1px rgba(15,23,42,0.08);">
          <div style="background: rgba(255,255,255,0.92); padding: 5px 10px; border-radius: 7px; font-weight: 700; font-size: 13.5px;">
            ${escapeHtml(s.serviceName)} (${s.count})
          </div>
          <strong style="font-size: 15px;" class="tabular-nums">${formatCurrencyBRL(s.totalRevenue)}</strong>
        </div>
      `).join('');
    } catch (err) {
      showToast(err.message);
    }
  }

  /* ==========================================================================
     SEÇÃO 4: LOJA (FUNCIONÁRIOS, PRODUTOS/SERVIÇOS E CONFIGURAÇÕES)
     ========================================================================== */
  async function loadStoreBarbers() {
    const container = document.getElementById('store-barbers-container');
    try {
      const list = await apiFetch('/api/dashboard/store/barbers');
      state.barbers = list;

      const apptBarberSelect = document.getElementById('appt-barber');
      if (apptBarberSelect) {
        apptBarberSelect.innerHTML = list
          .filter(b => b.isActive)
          .map(b => `<option value="${b.id}">${escapeHtml(b.name)} (${escapeHtml(b.workStartTime)} às ${escapeHtml(b.workEndTime)})</option>`)
          .join('');
      }

      if (!container) return;
      container.innerHTML = list.map(b => `
        <article class="crm-card">
          <div>
            <div style="display: flex; justify-content: space-between; align-items: center;">
              <h3 class="crm-customer-title">${escapeHtml(b.name)}</h3>
              <span class="crm-stat-pill" style="background: ${b.isActive ? '#ecfdf5' : '#fef2f2'}; color: ${b.isActive ? '#16a34a' : '#dc2626'};">
                ${b.isActive ? 'Ativo' : 'Inativo'}
              </span>
            </div>
            <p style="margin: 5px 0; font-size: 13px; color: var(--text-secondary);">
              ${escapeHtml(b.specialty)} ${b.phone ? `• ${escapeHtml(b.phone)}` : ''}
            </p>
            <div style="display: flex; gap: 6px; flex-wrap: wrap; margin-top: 8px;">
              <span class="crm-stat-pill">${icon('clock')} Horário: ${escapeHtml(b.workStartTime)} às ${escapeHtml(b.workEndTime)}</span>
              ${b.lunchStartTime && b.lunchEndTime ? `<span class="crm-stat-pill">${icon('utensils')} Almoço: ${escapeHtml(b.lunchStartTime)} às ${escapeHtml(b.lunchEndTime)}</span>` : ''}
              <span class="crm-stat-pill">Máx/hora: ${b.maxConcurrentPerSlot}</span>
            </div>
          </div>
          <div style="display: flex; gap: 8px;">
            <button type="button" class="btn btn-secondary" style="flex: 1;" data-edit-barber="${b.id}">Editar</button>
            <button type="button" class="btn btn-danger" data-del-barber="${b.id}">Remover</button>
          </div>
        </article>
      `).join('');

      container.querySelectorAll('[data-edit-barber]').forEach(btn => {
        btn.addEventListener('click', () => {
          const barber = state.barbers.find(x => x.id === Number(btn.dataset.editBarber));
          if (!barber) return;
          document.getElementById('barber-id').value = barber.id;
          document.getElementById('barber-name').value = barber.name;
          document.getElementById('barber-phone').value = barber.phone;
          document.getElementById('barber-specialty').value = barber.specialty;
          document.getElementById('barber-start').value = barber.workStartTime;
          document.getElementById('barber-end').value = barber.workEndTime;
          document.getElementById('barber-lunch-start').value = barber.lunchStartTime || '12:00';
          document.getElementById('barber-lunch-end').value = barber.lunchEndTime || '13:00';
          document.getElementById('barber-max-slot').value = barber.maxConcurrentPerSlot;
          document.getElementById('barber-active').value = String(barber.isActive);
          document.getElementById('dialog-barber-title').textContent = `Editar — ${barber.name}`;
          document.getElementById('dialog-barber').showModal();
        });
      });

      container.querySelectorAll('[data-del-barber]').forEach(btn => {
        btn.addEventListener('click', async () => {
          try {
            await apiFetch(`/api/dashboard/store/barbers/${btn.dataset.delBarber}`, { method: 'DELETE' });
            showToast('Funcionário removido.');
            loadStoreBarbers();
          } catch (err) {
            showToast(err.message);
          }
        });
      });
    } catch (err) {
      showToast(err.message);
    }
  }

  async function loadStoreServices() {
    const container = document.getElementById('store-services-container');
    try {
      const list = await apiFetch('/api/dashboard/store/services');
      state.services = list;

      const apptServiceSelect = document.getElementById('appt-service');
      if (apptServiceSelect) {
        apptServiceSelect.innerHTML = list
          .filter(s => s.isActive)
          .map(s => `<option value="${s.id}">${escapeHtml(s.name)} — ${formatCurrencyBRL(s.price)}</option>`)
          .join('');
      }

      if (!container) return;
      container.innerHTML = list.map(s => `
        <article class="crm-card" style="background: linear-gradient(90deg, ${escapeHtml(s.colorHex)} 0%, color-mix(in srgb, ${escapeHtml(s.colorHex)} 30%, #ffffff) 40%, #ffffff 100%);">
          <div style="background: rgba(255,255,255,0.93); padding: 12px; border-radius: 10px;">
            <div style="display: flex; justify-content: space-between; align-items: center;">
              <h3 class="crm-customer-title">${escapeHtml(s.name)}</h3>
              <strong style="font-size: 16px; color: #0f172a;" class="tabular-nums">${formatCurrencyBRL(s.price)}</strong>
            </div>
            <p style="margin: 4px 0 0; font-size: 12px; color: var(--text-secondary);">
              ${s.durationMinutes} min • Cor: <code>${escapeHtml(s.colorHex)}</code>
            </p>
          </div>
          <div style="display: flex; gap: 8px;">
            <button type="button" class="btn btn-secondary" style="flex: 1; background: rgba(255,255,255,0.92);" data-edit-service="${s.id}">Editar</button>
            <button type="button" class="btn btn-danger" data-del-service="${s.id}">Remover</button>
          </div>
        </article>
      `).join('');

      container.querySelectorAll('[data-edit-service]').forEach(btn => {
        btn.addEventListener('click', () => {
          const srv = state.services.find(x => x.id === Number(btn.dataset.editService));
          if (!srv) return;
          document.getElementById('service-id').value = srv.id;
          document.getElementById('service-name').value = srv.name;
          document.getElementById('service-price').value = srv.price;
          document.getElementById('service-duration').value = String(srv.durationMinutes);
          document.getElementById('service-color').value = srv.colorHex;
          document.getElementById('service-order').value = srv.displayOrder;
          document.getElementById('dialog-service-title').textContent = `Editar — ${srv.name}`;
          document.getElementById('dialog-service').showModal();
        });
      });

      container.querySelectorAll('[data-del-service]').forEach(btn => {
        btn.addEventListener('click', async () => {
          try {
            await apiFetch(`/api/dashboard/store/services/${btn.dataset.delService}`, { method: 'DELETE' });
            showToast('Serviço removido.');
            loadStoreServices();
          } catch (err) {
            showToast(err.message);
          }
        });
      });
    } catch (err) {
      showToast(err.message);
    }
  }

  async function loadStoreSettings() {
    try {
      const s = await apiFetch('/api/dashboard/store/settings');
      document.getElementById('cfg-store-name').value = s.name;
      document.getElementById('cfg-open-time').value = s.openingTime;
      document.getElementById('cfg-close-time').value = s.closingTime;
      document.getElementById('cfg-retention-days').value = s.evaluationRetentionDays;
      document.getElementById('cfg-poll-welcome').value = s.welcomePollTitle;
      document.getElementById('cfg-poll-service').value = s.servicePollTitle;
      document.getElementById('cfg-poll-barber').value = s.barberPollTitle;
      document.getElementById('cfg-poll-date').value = s.datePollTitle;
      document.getElementById('cfg-poll-time').value = s.timePollTitle;
      document.getElementById('cfg-ask-today').value = s.askNameTodayTemplate;
      document.getElementById('cfg-ask-other').value = s.askNameOtherDayTemplate;
      document.getElementById('cfg-poll-confirm').value = s.confirmationPollTemplate;
      document.getElementById('cfg-msg-confirmed').value = s.confirmedMessageTemplate;
      document.getElementById('cfg-msg-reminder').value = s.reminder1hTemplate;
      document.getElementById('cfg-poll-eval').value = s.evaluation2hPollTitle;
      document.getElementById('cfg-msg-eval-open').value = s.evaluationWindowOpenMessage;
    } catch (err) {
      showToast(err.message);
    }
  }

  /* ==========================================================================
     SEÇÃO 5: WHATSAPP BRIDGE (QR CODE REAL + PARING CODE 8 DÍGITOS POR NÚMERO)
     ========================================================================== */
  function renderPairingCodeDigitsHtml(rawCode, phoneText) {
    const clean = String(rawCode || '').replace(/[^A-Za-z0-9]/g, '').toUpperCase().slice(0, 8);
    if (!clean) {
      return `<span style="font-size: 13px; color: var(--text-secondary);">Digite o número acima para exibir os 8 dígitos</span>`;
    }

    const chars = clean.split('');
    const firstHalf = chars.slice(0, 4);
    const secondHalf = chars.slice(4, 8);

    const boxStyle = `
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 34px;
      height: 42px;
      border-radius: 8px;
      background: #0f172a;
      color: #ffffff;
      font-family: var(--font-mono);
      font-size: 18px;
      font-weight: 800;
      box-shadow: 0 2px 4px rgba(15,23,42,0.16);
    `;

    return `
      <div style="width: 100%;">
        <div style="font-size: 11.5px; font-weight: 700; color: #16a34a; margin-bottom: 8px;">
          Código de 8 dígitos para ${escapeHtml(phoneText || 'seu número')}:
        </div>
        <div style="display: flex; align-items: center; justify-content: center; gap: 5px; flex-wrap: wrap; margin-bottom: 10px;">
          ${firstHalf.map(c => `<span style="${boxStyle}">${escapeHtml(c)}</span>`).join('')}
          <span style="font-weight: 800; font-size: 18px; color: #64748b; padding: 0 2px;">-</span>
          ${secondHalf.map(c => `<span style="${boxStyle}">${escapeHtml(c)}</span>`).join('')}
        </div>
        <div style="display: flex; align-items: center; justify-content: center; gap: 8px;">
          <button type="button" id="btn-copy-pairing-code" data-code="${escapeHtml(rawCode)}" class="btn btn-secondary" style="min-height: 30px; padding: 4px 10px; font-size: 12px;">
            Copiar Código (${escapeHtml(rawCode)})
          </button>
        </div>
      </div>
    `;
  }

  async function loadWhatsAppStatus() {
    const statusBox = document.getElementById('whatsapp-status-box');
    const qrArea = document.getElementById('wa-qr-display-area');
    const pairingArea = document.getElementById('wa-pairing-display-area');
    const flowBox = document.getElementById('whatsapp-flow-preview');

    try {
      const st = await apiFetch('/api/dashboard/whatsapp/status');
      const isConnected = st.status === 'CONNECTED';
      const isQr = st.status === 'QR_READY';
      const isPairing = st.status === 'PAIRING_CODE_READY';
      const isDataUrlQr = st.lastQrCodeData && st.lastQrCodeData.startsWith('data:image/');

      const statusLabel = isConnected
        ? 'CONECTADO'
        : isQr
          ? 'QR CODE PRONTO PARA LEITURA'
          : isPairing
            ? 'AGUARDANDO DIGITAÇÃO DOS 8 DÍGITOS NO CELULAR'
            : 'DESCONECTADO';

      statusBox.innerHTML = `
        <div style="padding: 14px 16px; border-radius: 10px; background: var(--bg-elevated); border: 1px solid var(--border-subtle); display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px;">
          <div style="display: flex; align-items: center; gap: 12px; flex-wrap: wrap;">
            <span class="crm-stat-pill" style="background: ${isConnected ? '#ecfdf5' : (isQr || isPairing) ? '#fffbeb' : '#f1f5f9'}; color: ${isConnected ? '#16a34a' : (isQr || isPairing) ? '#d97706' : '#475569'};">
              ● ${escapeHtml(statusLabel)}
            </span>
            <span style="font-size: 14px; font-weight: 700; color: var(--text-primary);">
              Número: ${escapeHtml(st.connectedPhone || 'Nenhum aparelho vinculado')}
            </span>
          </div>
          <span style="font-size: 12px; color: var(--text-secondary);">
            Bridge: <code>${escapeHtml(st.bridgeEndpoint)}</code>
          </span>
        </div>
      `;

      if (qrArea) {
        if (isConnected) {
          qrArea.innerHTML = `
            <div style="color: #16a34a; font-weight: 700; font-size: 14px; display: inline-flex; align-items: center; gap: 6px;">
              ${icon('check-circle-2', 'ui-icon-success')} Aparelho conectado (${escapeHtml(st.connectedPhone || '')})
            </div>
          `;
        } else if (st.lastQrCodeData) {
          qrArea.innerHTML = isDataUrlQr
            ? `
              <img src="${st.lastQrCodeData}" alt="QR Code WhatsApp" style="width: 196px; height: 196px; display: block; margin: 0 auto; border-radius: 8px; border: 1px solid #e2e8f0;">
              <small style="display: block; margin-top: 8px; font-size: 11.5px; color: #16a34a; font-weight: 700;">Aponte a câmera do WhatsApp para ler</small>
            `
            : `
              <div style="width: 148px; height: 148px; margin: 0 auto; background: repeating-conic-gradient(#0f172a 0% 25%, #ffffff 0% 50%) 50% / 12px 12px; border-radius: 6px; display: grid; place-items: center;">
                <span style="background: #fff; padding: 4px 8px; border-radius: 4px; font-weight: 800; font-size: 11px;">QR CODE</span>
              </div>
              <small style="display: block; margin-top: 6px; font-size: 11px; color: var(--text-secondary);">${escapeHtml(st.lastQrCodeData)}</small>
            `;
        } else {
          qrArea.innerHTML = `
            <span style="font-size: 13px; color: var(--text-secondary);">Clique em <strong>Gerar QR Code</strong> abaixo para iniciar</span>
          `;
        }
      }

      if (pairingArea) {
        if (isConnected) {
          pairingArea.innerHTML = `
            <div style="color: #16a34a; font-weight: 700; font-size: 14px; display: inline-flex; align-items: center; gap: 6px;">
              ${icon('check-circle-2', 'ui-icon-success')} Número vinculado com sucesso!
            </div>
          `;
        } else if (st.lastPairingCode) {
          pairingArea.innerHTML = renderPairingCodeDigitsHtml(st.lastPairingCode, st.connectedPhone);
          const copyBtn = document.getElementById('btn-copy-pairing-code');
          if (copyBtn) {
            copyBtn.addEventListener('click', () => {
              navigator.clipboard?.writeText(copyBtn.dataset.code || '');
              showToast('Código de 8 dígitos copiado!');
            });
          }
        } else {
          pairingArea.innerHTML = `
            <span style="font-size: 13px; color: var(--text-secondary);">Digite o número acima para exibir os 8 dígitos</span>
          `;
        }
      }

      flowBox.innerHTML = st.activePollFlowPreview.map(step => `
        <div style="padding: 10px 12px; border-radius: 8px; background: var(--bg-elevated); margin-bottom: 6px; font-size: 13px; font-weight: 500;">
          ${escapeHtml(step)}
        </div>
      `).join('');

      renderWhatsAppBotPauseState(!!st.botPaused);
    } catch (err) {
      showToast(err.message);
    }
  }

  function renderWhatsAppBotPauseState(paused) {
    const btn = document.getElementById('btn-wa-bot-toggle');
    const label = document.getElementById('btn-wa-bot-toggle-label');
    const hint = document.getElementById('wa-bot-state-hint');
    if (btn) {
      btn.dataset.paused = String(paused);
      btn.setAttribute('aria-pressed', String(paused));
    }
    if (label) label.textContent = paused ? 'Retomar Bot' : 'Pausar Bot';
    if (hint) {
      hint.textContent = paused
        ? 'Bot PAUSADO: não responde clientes nem envia lembretes/avaliações automáticas. Você atende manualmente.'
        : 'Bot ativo: responde enquetes, envia lembretes 1h antes e pedidos de avaliação.';
    }
  }

  function setupPwaInstallAndServiceWorker() {
    if ('serviceWorker' in navigator) {
      navigator.serviceWorker.register('/sw.js').catch(() => {});
      navigator.serviceWorker.addEventListener('message', event => {
        if (event.data?.type === 'NOTIFICATION_CLICK' && event.data.topic) {
          const targetSection = event.data.topic === 'evaluations' ? 'crm' : event.data.topic;
          const btn = document.querySelector(`.nav-btn[data-section="${targetSection}"]`);
          btn?.click();
        }
      });
    }

    const installButton = document.querySelector('#install-app');
    const dialogInstallBox = document.querySelector('#pwa-direct-install-box');
    const dialogInstallBtn = document.querySelector('#btn-dialog-install-now');
    const installStatus = document.querySelector('#install-status');
    const supportsWebInstall = typeof Navigator.prototype.install === 'function';
    let deferredInstallPrompt;

    const revealInstallButtons = () => {
      if (installButton) installButton.hidden = false;
      if (dialogInstallBox) dialogInstallBox.hidden = false;
    };

    const hideInstallButtons = () => {
      if (installButton) installButton.hidden = true;
      if (dialogInstallBox) dialogInstallBox.hidden = true;
    };

    if (supportsWebInstall) {
      revealInstallButtons();
    }

    window.addEventListener('beforeinstallprompt', event => {
      event.preventDefault();
      deferredInstallPrompt = event;
      if (!supportsWebInstall) {
        revealInstallButtons();
      }
    });

    const handleInstallClick = async triggerBtn => {
      if (!triggerBtn || triggerBtn.getAttribute('aria-disabled') === 'true') {
        return;
      }
      // Usa aria-disabled em vez de disabled para não derrubar o foco (modern-web-guidance: install-web-app)
      triggerBtn.setAttribute('aria-disabled', 'true');

      try {
        if (supportsWebInstall) {
          await navigator.install();
          if (installStatus) installStatus.textContent = 'Aplicativo instalado ou aberto com sucesso!';
          hideInstallButtons();
        } else if (deferredInstallPrompt) {
          const promptEvent = deferredInstallPrompt;
          deferredInstallPrompt = undefined;
          await promptEvent.prompt();
          const { outcome } = await promptEvent.userChoice;

          if (installStatus) {
            installStatus.textContent =
              outcome === 'accepted'
                ? 'Aplicativo instalado na tela principal!'
                : 'Instalação cancelada.';
          }
          if (!deferredInstallPrompt) {
            hideInstallButtons();
          }
        }
      } catch (error) {
        if (error.name === 'AbortError') {
          if (installStatus) installStatus.textContent = 'A instalação foi cancelada ou não pôde ser concluída.';
        } else {
          if (installStatus) installStatus.textContent = 'Não foi possível iniciar a instalação automática.';
        }
      } finally {
        triggerBtn.removeAttribute('aria-disabled');
      }
    };

    installButton?.addEventListener('click', () => handleInstallClick(installButton));
    dialogInstallBtn?.addEventListener('click', () => handleInstallClick(dialogInstallBtn));
  }

  /* ==========================================================================
     INICIALIZAÇÃO E BINDINGS DE EVENTOS
     ========================================================================== */
  document.addEventListener('DOMContentLoaded', () => {
    setupDialogs();
    setupNavigation();
    setupPwaInstallAndServiceWorker();

    // Desbloqueia AudioContext no primeiro toque/clique para alertas sonoros no celular
    document.addEventListener('pointerdown', ensureAudioUnlocked, { once: true });
    document.addEventListener('keydown', ensureAudioUnlocked, { once: true });

    const todayIso = new Date().toISOString().slice(0, 10);
    document.getElementById('appt-date').value = todayIso;

    document.getElementById('login-form').addEventListener('submit', async e => {
      e.preventDefault();
      const errEl = document.getElementById('login-error');
      errEl.hidden = true;
      const username = document.getElementById('login-username').value;
      const password = document.getElementById('login-password').value;

      try {
        let res;
        try {
          res = await apiFetch('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) });
        } catch (first) {
          if (first.message !== 'TOTP_REQUIRED') throw first;
          // Segundo fator ativo (item 68): pede o código do aplicativo autenticador
          const totpCode = window.prompt('Digite o código de 6 dígitos do seu aplicativo autenticador:');
          if (!totpCode) throw new Error('Login cancelado: o código do segundo fator é obrigatório.');
          res = await apiFetch('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password, totpCode }) });
        }
        state.token = res.token;
        state.username = res.username;
        state.storeName = res.storeName;
        state.role = res.role || 'Admin';
        state.storeId = res.storeId || 1;
        state.targetStoreId = null;
        localStorage.setItem('ch_token', res.token);
        localStorage.setItem('ch_user', res.username);
        localStorage.setItem('ch_store', res.storeName);
        localStorage.setItem('ch_role', state.role);
        localStorage.setItem('ch_store_id', String(state.storeId));
        sessionStorage.setItem('ch_token', res.token);
        sessionStorage.setItem('ch_user', res.username);
        sessionStorage.setItem('ch_store', res.storeName);
        sessionStorage.setItem('ch_role', state.role);
        sessionStorage.setItem('ch_store_id', String(state.storeId));
        // Senha provisória: a troca é obrigatória antes de entrar no painel.
        if (res.mustChangePassword) {
          let changed = false;
          while (!changed) {
            changed = await forcePasswordChange(password);
            if (!changed) showToast('Você precisa definir uma nova senha para continuar.');
          }
        }
        showAuthenticatedApp();
      } catch (err) {
        errEl.textContent = err?.message && !err.message.startsWith('Erro 401')
          ? err.message
          : 'Usuário ou senha inválidos.';
        errEl.hidden = false;
      }
    });

    document.getElementById('btn-logout').addEventListener('click', handleLogout);
    document.getElementById('btn-mobile-logout')?.addEventListener('click', handleLogout);

    document.getElementById('btn-sound-toggle')?.addEventListener('click', toggleNotificationSound);
    document.getElementById('btn-dialog-sound-toggle')?.addEventListener('click', toggleNotificationSound);

    document.getElementById('btn-open-notifications')?.addEventListener('click', () => {
      state.unreadNotifications = 0;
      updateUnreadBadgeUi();
      updateDeviceNotificationPermissionUi();
      renderNotificationsHistory();
      document.getElementById('dialog-notifications')?.showModal();
    });

    document.getElementById('btn-enable-device-notif')?.addEventListener('click', async () => {
      if (!('Notification' in window)) {
        showToast('Notificações nativas não suportadas neste navegador.');
        return;
      }
      const perm = await Notification.requestPermission();
      updateDeviceNotificationPermissionUi();
      if (perm === 'granted') {
        playNotificationChime();
        dispatchDeviceNotification('Clinic Hair', 'Notificações ativadas com sucesso neste aparelho!', 'agenda');
      }
    });

    document.getElementById('btn-test-notification')?.addEventListener('click', async () => {
      ensureAudioUnlocked();
      const dlg = document.getElementById('dialog-notifications');
      if (dlg?.open) dlg.close();
      try {
        await apiFetch('/api/dashboard/notifications/test', { method: 'POST' });
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-open-pwa-guide')?.addEventListener('click', () => {
      document.getElementById('dialog-pwa-install')?.showModal();
    });

    // Sincroniza notificações em background a cada 5s (ideal para celular quando retorna do background)
    setInterval(() => {
      if (state.token) {
        syncRecentNotifications(false);
      }
    }, 5000);

    document.getElementById('agenda-filter-date').addEventListener('change', loadAppointments);
    document.getElementById('btn-agenda-show-all').addEventListener('click', () => {
      document.getElementById('agenda-filter-date').value = todayIsoLocal();
      loadAppointments();
    });

    // Filtro de avaliações (Todas / Positivo / Negativo)
    document.querySelectorAll('.eval-filter-btn').forEach(btn => {
      btn.addEventListener('click', () => {
        state.evalFilter = btn.dataset.evalFilter;
        renderEvaluationsList();
      });
    });

    document.getElementById('btn-open-manual-appt').addEventListener('click', async () => {
      await Promise.all([loadStoreBarbers(), loadStoreServices()]);
      await refreshManualAppointmentSlots();
      document.getElementById('dialog-manual-appt').showModal();
    });

    document.getElementById('appt-barber').addEventListener('change', refreshManualAppointmentSlots);
    document.getElementById('appt-date').addEventListener('change', refreshManualAppointmentSlots);

    document.getElementById('form-manual-appt').addEventListener('submit', async e => {
      e.preventDefault();
      try {
        const paymentMethod = document.querySelector('input[name="apptPaymentMethod"]:checked')?.value || 'PIX';
        await apiFetch('/api/dashboard/appointments', {
          method: 'POST',
          body: JSON.stringify({
            customerName: document.getElementById('appt-customer-name').value,
            customerPhone: document.getElementById('appt-customer-phone').value,
            serviceItemId: Number(document.getElementById('appt-service').value),
            barberId: Number(document.getElementById('appt-barber').value),
            appointmentDate: document.getElementById('appt-date').value,
            startTime: document.getElementById('appt-time').value,
            notes: document.getElementById('appt-notes').value,
            paymentMethod
          })
        });
        document.getElementById('dialog-manual-appt').close();
        document.getElementById('form-manual-appt').reset();
        document.getElementById('appt-date').value = todayIso;
        showToast('Agendamento registrado!');
        loadAppointments();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-cal-prev').addEventListener('click', () => {
      state.calMonth--;
      if (state.calMonth < 1) {
        state.calMonth = 12;
        state.calYear--;
      }
      loadCalendarMonth();
    });

    document.getElementById('btn-cal-next').addEventListener('click', () => {
      state.calMonth++;
      if (state.calMonth > 12) {
        state.calMonth = 1;
        state.calYear++;
      }
      loadCalendarMonth();
    });

    document.getElementById('btn-run-storage-cleanup').addEventListener('click', async () => {
      try {
        await apiFetch('/api/dashboard/crm/evaluations/cleanup', { method: 'POST' });
        showToast('Espaço otimizado!');
        loadAnonymousEvaluations();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-new-barber').addEventListener('click', () => {
      document.getElementById('form-barber').reset();
      document.getElementById('barber-id').value = '';
      document.getElementById('barber-start').value = '08:00';
      document.getElementById('barber-end').value = '19:00';
      document.getElementById('barber-lunch-start').value = '12:00';
      document.getElementById('barber-lunch-end').value = '13:00';
      document.getElementById('barber-max-slot').value = '3';
      document.getElementById('dialog-barber-title').textContent = 'Registrar Funcionário';
      document.getElementById('dialog-barber').showModal();
    });

    document.getElementById('form-barber').addEventListener('submit', async e => {
      e.preventDefault();
      const id = document.getElementById('barber-id').value;
      const payload = {
        name: document.getElementById('barber-name').value,
        phone: document.getElementById('barber-phone').value,
        specialty: document.getElementById('barber-specialty').value,
        workStartTime: document.getElementById('barber-start').value,
        workEndTime: document.getElementById('barber-end').value,
        lunchStartTime: document.getElementById('barber-lunch-start').value || '12:00',
        lunchEndTime: document.getElementById('barber-lunch-end').value || '13:00',
        workingDaysCsv: '1,2,3,4,5,6',
        maxConcurrentPerSlot: Number(document.getElementById('barber-max-slot').value),
        isActive: document.getElementById('barber-active').value === 'true'
      };

      try {
        await apiFetch(id ? `/api/dashboard/store/barbers/${id}` : '/api/dashboard/store/barbers', {
          method: id ? 'PUT' : 'POST',
          body: JSON.stringify(payload)
        });
        document.getElementById('dialog-barber').close();
        showToast('Funcionário salvo!');
        loadStoreBarbers();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-new-service').addEventListener('click', () => {
      document.getElementById('form-service').reset();
      document.getElementById('service-id').value = '';
      document.getElementById('service-color').value = '#FF6B00';
      document.getElementById('dialog-service-title').textContent = 'Novo Produto / Serviço';
      document.getElementById('dialog-service').showModal();
    });

    document.getElementById('form-service').addEventListener('submit', async e => {
      e.preventDefault();
      const id = document.getElementById('service-id').value;
      const payload = {
        name: document.getElementById('service-name').value,
        price: Number(document.getElementById('service-price').value),
        durationMinutes: Number(document.getElementById('service-duration').value),
        colorHex: document.getElementById('service-color').value,
        displayOrder: Number(document.getElementById('service-order').value),
        isActive: true
      };

      try {
        await apiFetch(id ? `/api/dashboard/store/services/${id}` : '/api/dashboard/store/services', {
          method: id ? 'PUT' : 'POST',
          body: JSON.stringify(payload)
        });
        document.getElementById('dialog-service').close();
        showToast('Serviço salvo!');
        loadStoreServices();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('store-settings-form').addEventListener('submit', async e => {
      e.preventDefault();
      try {
        const updated = await apiFetch('/api/dashboard/store/settings', {
          method: 'PUT',
          body: JSON.stringify({
            name: document.getElementById('cfg-store-name').value,
            openingTime: document.getElementById('cfg-open-time').value,
            closingTime: document.getElementById('cfg-close-time').value,
            workingDaysCsv: '1,2,3,4,5,6',
            evaluationRetentionDays: Number(document.getElementById('cfg-retention-days').value),
            welcomePollTitle: document.getElementById('cfg-poll-welcome').value,
            servicePollTitle: document.getElementById('cfg-poll-service').value,
            barberPollTitle: document.getElementById('cfg-poll-barber').value,
            datePollTitle: document.getElementById('cfg-poll-date').value,
            timePollTitle: document.getElementById('cfg-poll-time').value,
            askNameTodayTemplate: document.getElementById('cfg-ask-today').value,
            askNameOtherDayTemplate: document.getElementById('cfg-ask-other').value,
            confirmationPollTemplate: document.getElementById('cfg-poll-confirm').value,
            confirmedMessageTemplate: document.getElementById('cfg-msg-confirmed').value,
            reminder1hTemplate: document.getElementById('cfg-msg-reminder').value,
            evaluation2hPollTitle: document.getElementById('cfg-poll-eval').value,
            evaluationWindowOpenMessage: document.getElementById('cfg-msg-eval-open').value
          })
        });
        document.getElementById('header-store-name').textContent = updated.name;
        showToast('Configurações salvas!');
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-wa-start')?.addEventListener('click', async e => {
      const btn = e.currentTarget;
      const prevText = btn.textContent;
      btn.disabled = true;
      btn.textContent = 'Gerando QR Code...';
      try {
        await apiFetch('/api/dashboard/whatsapp/control/qrcode', { method: 'POST' });
        showToast('QR Code gerado!');
        await loadWhatsAppStatus();
      } catch (err) {
        showToast(err.message);
      } finally {
        btn.disabled = false;
        btn.textContent = prevText;
      }
    });

    const pairingPhoneInput = document.getElementById('wa-pairing-phone');
    pairingPhoneInput?.addEventListener('input', e => {
      e.target.value = e.target.value.replace(/[^\d+\s()-]/g, '').slice(0, 20);
    });

    // Atualiza o status do WhatsApp automaticamente a cada 3.5s quando a aba WhatsApp estiver aberta
    setInterval(() => {
      const waSection = document.getElementById('section-whatsapp');
      if (state.token && waSection && !waSection.hidden) {
        loadWhatsAppStatus();
      }
    }, 3500);

    document.getElementById('form-wa-pairing')?.addEventListener('submit', async e => {
      e.preventDefault();
      const phoneNumber = (pairingPhoneInput?.value || '').trim();
      if (!phoneNumber) return;

      const submitBtn = document.getElementById('btn-wa-pairing-submit');
      const prevText = submitBtn ? submitBtn.textContent : 'Gerar Código de 8 Dígitos';
      if (submitBtn) {
        submitBtn.disabled = true;
        submitBtn.textContent = 'Gerando 8 Dígitos...';
      }

      try {
        await apiFetch('/api/dashboard/whatsapp/pairing-code', {
          method: 'POST',
          body: JSON.stringify({ phoneNumber })
        });
        showToast('Código de 8 dígitos gerado!');
        await loadWhatsAppStatus();
      } catch (err) {
        showToast(err.message);
      } finally {
        if (submitBtn) {
          submitBtn.disabled = false;
          submitBtn.textContent = prevText;
        }
      }
    });

    document.getElementById('btn-wa-bot-toggle')?.addEventListener('click', async e => {
      const btn = e.currentTarget;
      const willPause = btn.dataset.paused !== 'true';
      const actionText = willPause ? 'pausar' : 'retomar';
      if (willPause && !confirm('Pausar o bot? Ele deixará de responder clientes e enviar lembretes/avaliações até você retomar.')) {
        return;
      }
      btn.disabled = true;
      try {
        const st = await apiFetch('/api/dashboard/whatsapp/bot/pause', {
          method: 'POST',
          body: JSON.stringify({ paused: willPause })
        });
        renderWhatsAppBotPauseState(!!st.botPaused);
        showToast(willPause ? 'Bot pausado.' : 'Bot retomado.');
      } catch (err) {
        showToast(`Não foi possível ${actionText} o bot: ${err.message}`);
      } finally {
        btn.disabled = false;
      }
    });

    document.getElementById('btn-wa-disconnect')?.addEventListener('click', async () => {
      await apiFetch('/api/dashboard/whatsapp/control/disconnect', { method: 'POST' });
      showToast('WhatsApp desconectado.');
      loadWhatsAppStatus();
    });

    // Bindings do Painel SuperAdministrador (Patricio)
    document.getElementById('btn-sa-return-overview')?.addEventListener('click', returnToSuperAdminDashboard);
    document.getElementById('btn-sa-refresh-overview')?.addEventListener('click', loadSuperAdminOverview);

    document.getElementById('btn-sa-open-create-store')?.addEventListener('click', () => {
      document.getElementById('form-sa-create-store')?.reset();
      document.getElementById('sa-new-due').value = new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10);
      document.getElementById('dialog-sa-create-store')?.showModal();
    });

    document.getElementById('form-sa-create-store')?.addEventListener('submit', async e => {
      e.preventDefault();
      try {
        const preset = document.getElementById('sa-new-preset').value;
        await apiFetch('/api/superadmin/stores', {
          method: 'POST',
          body: JSON.stringify({
            storeName: document.getElementById('sa-new-store-name').value,
            adminUsername: document.getElementById('sa-new-admin-user').value,
            adminPassword: document.getElementById('sa-new-admin-pass').value,
            ownerPhone: document.getElementById('sa-new-owner-phone').value,
            subscriptionPlan: document.getElementById('sa-new-plan').value,
            monthlyFee: Number(document.getElementById('sa-new-fee').value),
            nextDueDate: document.getElementById('sa-new-due').value,
            openingTime: document.getElementById('sa-new-open').value,
            closingTime: document.getElementById('sa-new-close').value,
            preconfigTemplate: preset === 'completo' ? 'vip' : 'standard'
          })
        });
        document.getElementById('dialog-sa-create-store')?.close();
        showToast('Nova loja criada e pré-configuração entregue com sucesso!');
        loadSuperAdminOverview();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('form-sa-preconfig')?.addEventListener('submit', async e => {
      e.preventDefault();
      const storeId = document.getElementById('sa-preconfig-store-id').value;
      const preset = document.getElementById('sa-preconfig-preset').value;
      try {
        await apiFetch(`/api/superadmin/stores/${storeId}/preconfig`, {
          method: 'POST',
          body: JSON.stringify({
            template: preset === 'completo' ? 'vip' : 'standard',
            resetPollMessages: document.getElementById('sa-preconfig-reset-polls').checked
          })
        });
        document.getElementById('dialog-sa-preconfig')?.close();
        showToast('Kit de pré-configuração entregue na loja!');
        loadSuperAdminOverview();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('form-sa-edit-store')?.addEventListener('submit', async e => {
      e.preventDefault();
      const storeId = document.getElementById('sa-edit-store-id').value;
      try {
        await apiFetch(`/api/superadmin/stores/${storeId}`, {
          method: 'PUT',
          body: JSON.stringify({
            storeName: document.getElementById('sa-edit-name').value,
            subscriptionPlan: document.getElementById('sa-edit-plan').value,
            monthlyFee: Number(document.getElementById('sa-edit-fee').value),
            subscriptionStatus: document.getElementById('sa-edit-sub-status').value,
            nextDueDate: document.getElementById('sa-edit-due').value,
            isActive: document.getElementById('sa-edit-active').value === 'true',
            ownerPhone: document.getElementById('sa-edit-owner-phone').value,
            newAdminPassword: document.getElementById('sa-edit-new-pass').value || null
          })
        });
        document.getElementById('dialog-sa-edit-store')?.close();
        showToast('Configurações da loja atualizadas!');
        loadSuperAdminOverview();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-sa-delete-store')?.addEventListener('click', async () => {
      const storeId = Number(document.getElementById('sa-edit-store-id').value);
      if (storeId <= 1) return;
      try {
        await apiFetch(`/api/superadmin/stores/${storeId}`, { method: 'DELETE' });
        document.getElementById('dialog-sa-edit-store')?.close();
        showToast('Loja removida com sucesso.');
        loadSuperAdminOverview();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('btn-sa-open-charge-modal')?.addEventListener('click', () => {
      openSuperAdminChargeModal(null, 149.90, null);
    });

    document.getElementById('btn-sa-QuickCharge-from-fin')?.addEventListener('click', () => {
      openSuperAdminChargeModal(null, 149.90, null);
    });

    document.getElementById('sa-charge-store')?.addEventListener('change', e => {
      const opt = e.target.selectedOptions?.[0];
      if (opt?.dataset?.fee) {
        document.getElementById('sa-charge-amount').value = opt.dataset.fee;
      }
    });

    document.getElementById('form-sa-charge')?.addEventListener('submit', async e => {
      e.preventDefault();
      try {
        const refMonth = document.getElementById('sa-charge-month').value;
        const method = document.getElementById('sa-charge-method').value;
        await apiFetch('/api/superadmin/billing/charge', {
          method: 'POST',
          body: JSON.stringify({
            storeId: Number(document.getElementById('sa-charge-store').value),
            description: `Assinatura Mensal (${refMonth} • ${method})`,
            amount: Number(document.getElementById('sa-charge-amount').value),
            dueDate: document.getElementById('sa-charge-due').value,
            paymentLinkOrPix: document.getElementById('sa-charge-pix').value,
            sendWhatsAppNotification: document.getElementById('sa-charge-send-wa').checked
          })
        });
        document.getElementById('dialog-sa-charge')?.close();
        showToast('Cobrança de assinatura emitida com sucesso!');
        loadSuperAdminOverview();
      } catch (err) {
        showToast(err.message);
      }
    });

    document.getElementById('sa-billing-store-filter')?.addEventListener('change', loadSuperAdminBillingOnly);
    document.getElementById('sa-log-level-filter')?.addEventListener('change', loadSuperAdminLogsOnly);
    document.getElementById('sa-log-store-filter')?.addEventListener('change', loadSuperAdminLogsOnly);
    document.getElementById('sa-log-search')?.addEventListener('input', loadSuperAdminLogsOnly);
    document.getElementById('btn-sa-refresh-logs')?.addEventListener('click', loadSuperAdminLogsOnly);

    document.getElementById('btn-sa-clear-logs')?.addEventListener('click', async () => {
      try {
        await apiFetch('/api/superadmin/logs/clear', { method: 'POST' });
        showToast('Histórico de logs limpo!');
        loadSuperAdminLogsOnly();
      } catch (err) {
        showToast(err.message);
      }
    });

    if (state.token) {
      showAuthenticatedApp();
    }
  });

  // Ponte para os módulos complementares (js/ext-*.js)
  window.CH = {
    state, apiFetch, showToast, escapeHtml, formatCurrencyBRL, formatDateBR, icon,
    isSuperAdmin: () => isSuperAdminUser(),
    goTo: (section) => activateNavSection(section)
  };
})();

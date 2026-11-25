/* Clinic Hair — experiência de uso (itens 77 a 84, 98 e 100):
   busca global, atalhos de teclado, modo escuro, preferências de notificação por tipo,
   acessibilidade, idioma (pt-BR/en) e identidade visual da loja. */
(() => {
  'use strict';
  const CH = window.CH;
  if (!CH) return;
  const h = (v) => CH.escapeHtml(v == null ? '' : String(v));
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* armazenamento indisponível */ } }
  };

  /* ---------------------------------------------------------------- preferências de notificação (83) */
  const topics = [
    { key: 'agenda', label: 'Agendamentos' },
    { key: 'whatsapp', label: 'WhatsApp e bot' },
    { key: 'superadmin', label: 'SuperAdmin' },
    { key: 'other', label: 'Outros' }
  ];
  window.CHPrefs = {
    forTopic(topic) {
      const all = store.get('ch_notif_prefs', {});
      const key = topics.some(t => t.key === topic) ? topic : 'other';
      return { sound: true, device: true, toast: true, ...(all[key] || {}) };
    }
  };
  function injectNotifPrefs() {
    const dlg = document.getElementById('dialog-notifications');
    const body = dlg?.querySelector('.dialog-body');
    if (!body || body.querySelector('.ext-notif-prefs')) return;
    const all = store.get('ch_notif_prefs', {});
    const box = document.createElement('div');
    box.className = 'ext-notif-prefs surface-card';
    box.innerHTML = `<h3>Avisos por tipo</h3><table class="ext-table"><thead><tr><th>Tipo</th><th>Som</th><th>Pop-up</th><th>Aparelho</th></tr></thead><tbody>
      ${topics.map(t => { const p = { sound: true, toast: true, device: true, ...(all[t.key] || {}) }; return `<tr><td>${t.label}</td>${['sound', 'toast', 'device'].map(k => `<td><input type="checkbox" aria-label="${t.label}: ${k}" data-topic="${t.key}" data-kind="${k}" ${p[k] ? 'checked' : ''}></td>`).join('')}</tr>`; }).join('')}
      </tbody></table>`;
    box.addEventListener('change', (e) => {
      const cb = e.target.closest('[data-topic]');
      if (!cb) return;
      const cur = store.get('ch_notif_prefs', {});
      cur[cb.dataset.topic] = { ...(cur[cb.dataset.topic] || {}), [cb.dataset.kind]: cb.checked };
      store.set('ch_notif_prefs', cur);
    });
    body.prepend(box);
  }

  /* ---------------------------------------------------------------- tema padrão (segue o aparelho)
     Sem alternância manual de modo claro/escuro: o painel usa sempre o tema padrão. */
  document.documentElement.removeAttribute('data-theme');

  /* ---------------------------------------------------------------- busca global (82) */
  let searchTimer = null;
  function openSearch() {
    let dlg = document.getElementById('ext-search');
    if (!dlg) {
      dlg = document.createElement('dialog');
      dlg.id = 'ext-search';
      dlg.className = 'ext-dialog';
      dlg.setAttribute('aria-label', 'Busca');
      dlg.innerHTML = `<div class="dialog-body"><input type="search" class="ext-search-input" placeholder="Buscar cliente, telefone, profissional, serviço, produto…" aria-label="Buscar" autocomplete="off">
        <ul class="ext-list ext-search-results" role="listbox"></ul><p class="ext-hint">Enter abre o primeiro resultado · Esc fecha · Ctrl+K abre de qualquer lugar</p></div>`;
      document.body.appendChild(dlg);
      dlg.addEventListener('click', (e) => { if (e.target === dlg) dlg.close(); });
      const input = dlg.querySelector('input');
      const list = dlg.querySelector('ul');
      input.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(async () => {
          const q = input.value.trim();
          if (q.length < 2) { list.innerHTML = ''; return; }
          try {
            const hits = await CH.apiFetch(`/api/dashboard/search?q=${encodeURIComponent(q)}`);
            list.innerHTML = hits.length ? hits.map((x, i) => `<li><button type="button" class="ext-hit" data-i="${i}"><span class="ext-pill">${h(x.kind)}</span> <strong>${h(x.title)}</strong> <small>${h(x.subtitle)}</small></button></li>`).join('') : '<li class="ext-hint">Nada encontrado.</li>';
            list._hits = hits;
          } catch (err) { list.innerHTML = `<li class="ext-hint">${h(err.message)}</li>`; }
        }, 220);
      });
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') list.querySelector('.ext-hit')?.click(); });
      list.addEventListener('click', (e) => {
        const b = e.target.closest('.ext-hit');
        if (!b) return;
        const hit = list._hits[Number(b.dataset.i)];
        dlg.close();
        if (hit.kind === 'cliente') { CH.goTo('crm'); window.CHOpenProfile?.(Number(hit.id)); return; }
        if (hit.section.startsWith('agenda')) {
          CH.goTo('agenda');
          const d = hit.section.split(':')[1];
          const dateInput = document.getElementById('agenda-filter-date');
          if (d && dateInput) { dateInput.value = d; dateInput.dispatchEvent(new Event('change', { bubbles: true })); }
          return;
        }
        CH.goTo(hit.section);
      });
    }
    dlg.showModal();
    const input = dlg.querySelector('input');
    input.value = '';
    dlg.querySelector('ul').innerHTML = '';
    input.focus();
  }

  /* ---------------------------------------------------------------- atalhos (81) */
  const shortcuts = { a: 'agenda', c: 'crm', r: 'relatorios', l: 'loja', w: 'whatsapp' };
  let gPressed = 0;
  document.addEventListener('keydown', (e) => {
    if (document.getElementById('app-shell')?.hidden) return;
    const typing = e.target.closest('input, textarea, select, [contenteditable="true"]');
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); openSearch(); return; }
    if (typing || e.ctrlKey || e.metaKey || e.altKey || document.querySelector('dialog[open]')) return;
    if (e.key === '/') { e.preventDefault(); openSearch(); return; }
    if (e.key === '?') { showHelp(); return; }
    if (e.key.toLowerCase() === 'n') { document.getElementById('btn-open-manual-appt')?.click(); return; }
    if (e.key.toLowerCase() === 'g') { gPressed = Date.now(); return; }
    if (Date.now() - gPressed < 1200 && shortcuts[e.key.toLowerCase()]) { CH.goTo(shortcuts[e.key.toLowerCase()]); gPressed = 0; }
  });
  function showHelp() {
    const dlg = document.createElement('dialog');
    dlg.className = 'ext-dialog';
    dlg.innerHTML = `<div class="dialog-header"><h2>Atalhos de teclado</h2><button type="button" class="btn btn-secondary" data-x aria-label="Fechar">✕</button></div>
      <div class="dialog-body"><table class="ext-table"><tbody>
      <tr><td><kbd>Ctrl</kbd>+<kbd>K</kbd> ou <kbd>/</kbd></td><td>Busca global</td></tr><tr><td><kbd>N</kbd></td><td>Novo agendamento</td></tr>
      <tr><td><kbd>G</kbd> depois <kbd>A</kbd></td><td>Agenda</td></tr><tr><td><kbd>G</kbd> depois <kbd>C</kbd></td><td>CRM</td></tr>
      <tr><td><kbd>G</kbd> depois <kbd>R</kbd></td><td>Relatórios</td></tr><tr><td><kbd>G</kbd> depois <kbd>L</kbd></td><td>Loja</td></tr>
      <tr><td><kbd>G</kbd> depois <kbd>W</kbd></td><td>WhatsApp</td></tr><tr><td><kbd>?</kbd></td><td>Esta ajuda</td></tr></tbody></table></div>`;
    document.body.appendChild(dlg);
    dlg.addEventListener('click', (e) => { if (e.target === dlg || e.target.closest('[data-x]')) dlg.close(); });
    dlg.addEventListener('close', () => dlg.remove());
    dlg.showModal();
  }

  /* ---------------------------------------------------------------- idioma (98) */
  const en = {
    'Agenda': 'Schedule', 'CRM': 'Customers', 'Relatórios': 'Reports', 'Loja': 'Store', 'WhatsApp': 'WhatsApp',
    'Calendário': 'Calendar', 'Quadro': 'Board', 'Bloqueios': 'Blocks', 'Lista de espera': 'Waitlist',
    'Clientes': 'Customers', 'Avaliações': 'Reviews', 'Inativos': 'Inactive', 'Campanhas': 'Campaigns', 'Duplicados': 'Duplicates', 'Notas': 'Ratings',
    'Funcionários': 'Staff', 'Produtos/Serviços': 'Services', 'Configurações': 'Settings', 'Planos': 'Plans', 'Estoque': 'Stock', 'Recursos': 'Features',
    'Novo Agendamento': 'New appointment', 'Todos': 'All', 'Sair': 'Log out', 'Buscar': 'Search', 'Salvar': 'Save', 'Atualizar': 'Refresh',
    'Relatórios e Financeiro': 'Reports & Finance', 'Saúde da conexão': 'Connection health', 'Fila de atendimento humano': 'Human support queue',
    'Respostas rápidas': 'Quick replies', 'Automação do bot': 'Bot automation', 'Fechamento de caixa': 'Cash closing', 'Assinaturas': 'Subscriptions',
    'Planos de assinatura': 'Subscription plans', 'Assinantes': 'Subscribers', 'Pedidos de cadastro': 'Sign-up requests', 'Cadastros': 'Sign-ups'
  };
  function translate(lang) {
    if (lang !== 'en') return;
    const walker = document.createTreeWalker(document.getElementById('app-shell') || document.body, NodeFilter.SHOW_TEXT);
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    for (const n of nodes) {
      const t = n.nodeValue.trim();
      if (t && en[t]) n.nodeValue = n.nodeValue.replace(t, en[t]);
    }
    document.documentElement.lang = 'en';
  }

  /* ---------------------------------------------------------------- marca própria (100) */
  window.CHBrand = {
    apply(s) {
      if (!s) return;
      const root = document.documentElement;
      if (s.brandColor && /^#[0-9a-f]{6}$/i.test(s.brandColor)) {
        root.style.setProperty('--brand-primary', s.brandColor);
        root.style.setProperty('--brand-primary-hover', s.brandColor);
      }
      const name = document.getElementById('header-store-name');
      if (name && s.brandDisplayName && !CH.state.targetStoreId) name.textContent = s.brandDisplayName;
      const logoHost = document.querySelector('.store-title')?.parentElement;
      if (logoHost && s.logoUrl && !logoHost.querySelector('.ext-logo')) {
        const img = document.createElement('img');
        img.className = 'ext-logo';
        img.alt = '';
        img.src = s.logoUrl;
        img.referrerPolicy = 'no-referrer';
        logoHost.prepend(img);
      }
      if (s.language === 'en') setTimeout(() => translate('en'), 50);
    }
  };

  /* ---------------------------------------------------------------- acessibilidade (84) e mobile (77) */
  function enhanceA11y() {
    const t = document.getElementById('toast-banner');
    if (t) { t.setAttribute('role', 'status'); t.setAttribute('aria-live', 'polite'); }
    if (!document.querySelector('.ext-skip')) {
      const skip = document.createElement('a');
      skip.href = '#';
      skip.className = 'ext-skip';
      skip.textContent = 'Pular para o conteúdo';
      skip.addEventListener('click', (e) => {
        e.preventDefault();
        const sec = document.querySelector('.main-section:not([hidden])');
        sec?.setAttribute('tabindex', '-1');
        sec?.focus();
      });
      document.body.prepend(skip);
    }
    document.querySelectorAll('.icon-action-btn:not([aria-label])').forEach(b => b.setAttribute('aria-label', b.title || 'Ação'));
  }

  function addHeaderTools() {
    const anchor = document.getElementById('btn-open-notifications');
    if (!anchor || document.getElementById('ext-search-btn')) return;
    const search = document.createElement('button');
    search.type = 'button';
    search.id = 'ext-search-btn';
    search.className = 'icon-action-btn';
    search.title = 'Buscar (Ctrl+K)';
    search.setAttribute('aria-label', 'Buscar (Ctrl+K)');
    search.textContent = '🔍';
    search.addEventListener('click', openSearch);
    anchor.before(search);
  }

  document.addEventListener('ch:app-ready', async () => {
    addHeaderTools();
    enhanceA11y();
    injectNotifPrefs();
    try { window.CHBrand.apply(await CH.apiFetch('/api/dashboard/extensions/settings')); } catch { /* sem configurações */ }
  });
})();

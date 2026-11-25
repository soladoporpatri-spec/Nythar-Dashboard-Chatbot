/* Clinic Hair — telas dos recursos do plano de melhorias (itens 11 a 100).
   Usa a ponte window.CH exposta por dashboard.js. Sem dependências externas. */
(() => {
  'use strict';
  const CH = window.CH;
  if (!CH) return;
  const h = (v) => CH.escapeHtml(v == null ? '' : String(v));
  const money = (v) => CH.formatCurrencyBRL(v);
  const dateBR = (v) => CH.formatDateBR(v);
  const toast = (m) => CH.showToast(m);
  const get = (url) => CH.apiFetch(url);
  const send = (method, url, body) => CH.apiFetch(url, { method, body: body === undefined ? undefined : JSON.stringify(body) });
  const todayIso = () => new Date(Date.now() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 10);
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const loaders = {};
  let barbersCache = null;
  let servicesCache = null;

  async function barbers() { if (!barbersCache) barbersCache = await get('/api/dashboard/store/barbers').catch(() => []); return barbersCache; }
  async function services() { if (!servicesCache) servicesCache = await get('/api/dashboard/store/services').catch(() => []); return servicesCache; }
  document.addEventListener('ch:section', () => { barbersCache = null; servicesCache = null; });

  /* ------------------------------------------------------------------ infraestrutura de abas e blocos */
  function addSubtab(sectionId, key, label, loader) {
    const section = document.getElementById(sectionId);
    const bar = section?.querySelector('.subtab-bar[role="tablist"]');
    if (!section || !bar) return null;
    const panelId = `panel-x-${key}`;
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.setAttribute('role', 'tab');
    btn.id = `tab-x-${key}`;
    btn.className = 'subtab-btn';
    btn.setAttribute('aria-selected', 'false');
    btn.dataset.subpanel = panelId;
    btn.textContent = label;
    bar.appendChild(btn);
    const panel = document.createElement('div');
    panel.id = panelId;
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', btn.id);
    panel.hidden = true;
    panel.innerHTML = '<div class="panel-inner ext-panel"></div>';
    section.appendChild(panel);
    loaders[panelId] = () => loader(panel.firstElementChild);
    return panel.firstElementChild;
  }

  function addBlock(sectionId, key, loader) {
    const section = document.getElementById(sectionId);
    if (!section) return null;
    const block = document.createElement('div');
    block.className = 'panel-inner ext-panel';
    block.id = `block-x-${key}`;
    section.appendChild(block);
    const run = () => loader(block);
    document.addEventListener('ch:section', (e) => { if (`section-${e.detail}` === sectionId) run(); });
    return block;
  }

  // Troca de sub-aba que também cobre as abas criadas aqui (as nativas são ligadas só na carga do dashboard.js)
  document.addEventListener('click', (e) => {
    const tab = e.target.closest('.subtab-btn[role="tab"][data-subpanel]');
    if (!tab) return;
    const bar = tab.closest('[role="tablist"]');
    $$('.subtab-btn[role="tab"]', bar).forEach(t => {
      const sel = t === tab;
      t.setAttribute('aria-selected', sel ? 'true' : 'false');
      const p = document.getElementById(t.dataset.subpanel);
      if (p) p.hidden = !sel;
    });
    loaders[tab.dataset.subpanel]?.();
  });

  /* ------------------------------------------------------------------ helpers de UI */
  function empty(text, actionLabel, actionAttr) {
    return `<div class="ext-empty"><p>${h(text)}</p>${actionLabel ? `<button type="button" class="btn btn-primary" ${actionAttr}>${h(actionLabel)}</button>` : ''}</div>`;
  }

  function field(f, v) {
    const id = `xf-${f.key}-${Math.random().toString(36).slice(2, 7)}`;
    const val = v ?? f.def ?? '';
    let input;
    if (f.type === 'checkbox') {
      return `<label class="ext-check"><input type="checkbox" name="${f.key}" ${val ? 'checked' : ''}> ${h(f.label)}</label>`;
    } else if (f.type === 'textarea') {
      input = `<textarea id="${id}" name="${f.key}" rows="${f.rows || 3}" ${f.max ? `maxlength="${f.max}"` : ''}>${h(val)}</textarea>`;
    } else if (f.type === 'select') {
      input = `<select id="${id}" name="${f.key}" ${f.multiple ? 'multiple size="5"' : ''}>${f.options.map(o => {
        const selected = f.multiple ? (Array.isArray(val) && val.map(String).includes(String(o.value))) : String(o.value) === String(val);
        return `<option value="${h(o.value)}" ${selected ? 'selected' : ''}>${h(o.label)}</option>`;
      }).join('')}</select>`;
    } else {
      input = `<input id="${id}" name="${f.key}" type="${f.type || 'text'}" value="${h(val)}" ${f.step ? `step="${f.step}"` : ''} ${f.min != null ? `min="${f.min}"` : ''} ${f.max ? `max="${f.max}"` : ''} ${f.placeholder ? `placeholder="${h(f.placeholder)}"` : ''} ${f.required ? 'required' : ''}>`;
    }
    return `<div class="field"><label for="${id}">${h(f.label)}</label>${input}${f.hint ? `<small class="ext-hint">${h(f.hint)}</small>` : ''}</div>`;
  }

  function form(fields, values = {}) {
    return `<div class="ext-form">${fields.map(f => field(f, values[f.key])).join('')}</div>`;
  }

  function read(root, fields) {
    const out = {};
    for (const f of fields) {
      const el = root.querySelector(`[name="${f.key}"]`);
      if (!el) continue;
      if (f.type === 'checkbox') out[f.key] = el.checked;
      else if (f.type === 'number') out[f.key] = el.value === '' ? (f.nullable ? null : 0) : Number(el.value);
      else if (f.type === 'select' && f.multiple) out[f.key] = Array.from(el.selectedOptions).map(o => Number(o.value));
      else if (f.type === 'select' && f.numeric) out[f.key] = el.value === '' ? null : Number(el.value);
      else out[f.key] = el.value.trim() === '' ? null : el.value.trim();
    }
    return out;
  }

  function dialog(title, body, { wide = false, footer = '' } = {}) {
    const dlg = document.createElement('dialog');
    dlg.className = `ext-dialog${wide ? ' ext-dialog-wide' : ''}`;
    dlg.setAttribute('aria-label', title);
    dlg.innerHTML = `<div class="dialog-header"><h2>${h(title)}</h2><button type="button" class="btn btn-secondary" data-x-close aria-label="Fechar">✕</button></div>
      <div class="dialog-body">${body}</div>${footer ? `<div class="dialog-footer">${footer}</div>` : ''}`;
    document.body.appendChild(dlg);
    dlg.addEventListener('click', (e) => { if (e.target.closest('[data-x-close]') || e.target === dlg) dlg.close(); });
    dlg.addEventListener('close', () => dlg.remove());
    dlg.showModal();
    return dlg;
  }

  // Ação com "Desfazer" no lugar do confirm() (item 79): só executa depois de 5s se ninguém desfizer
  function undoable(message, commit) {
    let host = document.getElementById('ext-undo-host');
    if (!host) {
      host = document.createElement('div');
      host.id = 'ext-undo-host';
      host.setAttribute('aria-live', 'polite');
      document.body.appendChild(host);
    }
    const item = document.createElement('div');
    item.className = 'ext-undo';
    item.innerHTML = `<span>${h(message)}</span><button type="button" class="btn btn-secondary">Desfazer</button>`;
    host.appendChild(item);
    let cancelled = false;
    item.querySelector('button').addEventListener('click', () => { cancelled = true; item.remove(); toast('Ação desfeita.'); });
    setTimeout(async () => {
      item.remove();
      if (cancelled) return;
      try { await commit(); } catch (err) { toast(err.message); }
    }, 5000);
  }
  window.CHUndo = undoable;

  const guard = (fn) => async (...a) => { try { await fn(...a); } catch (err) { toast(err.message); } };
  const statusPt = { Active: 'Ativa', Paused: 'Pausada', Overdue: 'Em atraso', Cancelled: 'Cancelada', Waiting: 'Aguardando', Notified: 'Avisado', Booked: 'Agendado', Removed: 'Removido', Queued: 'Na fila', Sending: 'Enviando', Done: 'Concluída', Pending: 'Pendente', Approved: 'Aprovado', Rejected: 'Recusado' };
  const pill = (s) => `<span class="ext-pill ext-pill-${h(String(s).toLowerCase())}">${h(statusPt[s] || s)}</span>`;
  const dows = ['Domingo', 'Segunda', 'Terça', 'Quarta', 'Quinta', 'Sexta', 'Sábado'];

  /* ================================================================== WHATSAPP (11–24) */
  addBlock('section-whatsapp', 'bot', async (root) => {
    root.innerHTML = `
      <div class="ext-grid-2">
        <div class="surface-card"><h3>Saúde da conexão</h3><div data-x="health">Carregando…</div></div>
        <div class="surface-card"><h3>Fila de atendimento humano</h3>
          <div class="ext-toolbar"><select data-x="conv-filter"><option value="queue">Aguardando equipe</option><option value="takeover">Em atendimento humano</option><option value="all">Todas recentes</option></select>
          <button type="button" class="btn btn-secondary" data-x="conv-refresh">Atualizar</button></div>
          <div data-x="convs"></div></div>
      </div>
      <div class="surface-card"><h3>Respostas rápidas</h3><p class="ext-hint">Quando o cliente escreve uma das palavras-chave, o bot responde na hora. Use {Endereço}, {Link do mapa} e {Loja}.</p>
        <div data-x="qr"></div><button type="button" class="btn btn-primary" data-x="qr-add">Nova resposta rápida</button></div>
      <div class="surface-card"><h3>Automação do bot</h3><div data-x="settings">Carregando…</div></div>`;

    const health = $('[data-x="health"]', root);
    get('/api/dashboard/bot/health').then(d => {
      health.innerHTML = `<div class="kpi-grid ext-kpis">
        <div class="kpi-card"><div class="kpi-label">Bridge</div><div class="kpi-value">${d.bridgeOnline ? '🟢 Online' : '🔴 Offline'}</div></div>
        <div class="kpi-card"><div class="kpi-label">WhatsApp</div><div class="kpi-value">${h(d.whatsAppStatus)}</div></div>
        <div class="kpi-card"><div class="kpi-label">Mensagens (1h)</div><div class="kpi-value">${d.webhooksLastHour}</div></div>
        <div class="kpi-card"><div class="kpi-label">Aguardando equipe</div><div class="kpi-value">${d.awaitingHumanCount}</div></div></div>
        <p class="ext-hint">Último evento: ${d.lastWebhookAt ? new Date(d.lastWebhookAt).toLocaleString('pt-BR') : '—'} ${d.botPaused ? '· <strong>Bot pausado</strong>' : ''} ${d.scheduledPauseActive ? '· <strong>Pausa programada ativa</strong>' : ''}</p>
        ${(d.recentIssues || []).length ? `<ul class="ext-list">${d.recentIssues.map(i => `<li><small>${new Date(i.at).toLocaleString('pt-BR')}</small> ${h(i.message)}</li>`).join('')}</ul>` : ''}`;
    }).catch(err => { health.textContent = err.message; });

    const loadConvs = guard(async () => {
      const list = await get(`/api/dashboard/bot/conversations?filter=${$('[data-x="conv-filter"]', root).value}`);
      const box = $('[data-x="convs"]', root);
      box.innerHTML = list.length ? `<ul class="ext-list">${list.map(c => `<li class="ext-row">
          <div><strong>${h(c.customerName || c.phone)}</strong> <small>${h(c.phone)}</small><br><small>${h(c.lastMessage || '')}</small></div>
          <div>${c.awaitingHuman ? pill('Pending') : ''}${c.humanTakeover ? '<span class="ext-pill">Humano</span>' : ''}
          <button type="button" class="btn btn-secondary btn-compact" data-open-conv="${h(c.phone)}">Abrir</button></div></li>`).join('')}</ul>`
        : empty('Nenhuma conversa aguardando a equipe. 🎉');
    });
    $('[data-x="conv-filter"]', root).addEventListener('change', loadConvs);
    $('[data-x="conv-refresh"]', root).addEventListener('click', loadConvs);
    root.addEventListener('click', guard(async (e) => {
      const b = e.target.closest('[data-open-conv]');
      if (b) await openConversation(b.dataset.openConv, loadConvs);
    }));
    loadConvs();

    const loadQr = guard(async () => {
      const list = await get('/api/dashboard/bot/quick-replies');
      $('[data-x="qr"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(q => `<li class="ext-row"><div><strong>${h(q.keywords)}</strong><br><small>${h(q.response)}</small></div>
        <div>${q.isActive ? '' : '<span class="ext-pill">Inativa</span>'}<button type="button" class="btn btn-secondary btn-compact" data-qr-edit="${q.id}">Editar</button>
        <button type="button" class="btn btn-danger btn-compact" data-qr-del="${q.id}">Excluir</button></div></li>`).join('')}</ul>` : empty('Nenhuma resposta rápida cadastrada.');
      root._qr = list;
    });
    const qrFields = [
      { key: 'keywords', label: 'Palavras-chave (separadas por vírgula)', placeholder: 'preço, valor, quanto custa', required: true },
      { key: 'response', label: 'Resposta', type: 'textarea', rows: 4, max: 1000 },
      { key: 'isActive', label: 'Ativa', type: 'checkbox', def: true }
    ];
    const editQr = (q) => {
      const dlg = dialog(q ? 'Editar resposta rápida' : 'Nova resposta rápida', form(qrFields, q || { isActive: true }), { footer: '<button type="button" class="btn btn-primary" data-save>Salvar</button>' });
      $('[data-save]', dlg).addEventListener('click', guard(async () => {
        const body = read(dlg, qrFields);
        await send(q ? 'PUT' : 'POST', q ? `/api/dashboard/bot/quick-replies/${q.id}` : '/api/dashboard/bot/quick-replies', body);
        dlg.close(); toast('Resposta salva.'); loadQr();
      }));
    };
    $('[data-x="qr-add"]', root).addEventListener('click', () => editQr(null));
    root.addEventListener('click', (e) => {
      const ed = e.target.closest('[data-qr-edit]');
      if (ed) editQr(root._qr.find(x => x.id === Number(ed.dataset.qrEdit)));
      const del = e.target.closest('[data-qr-del]');
      if (del) {
        del.closest('li').style.opacity = '.4';
        undoable('Resposta rápida será excluída.', async () => { await send('DELETE', `/api/dashboard/bot/quick-replies/${del.dataset.qrDel}`); loadQr(); });
      }
    });
    loadQr();

    const settingsFields = [
      { key: 'botScheduleEnabled', label: 'Pausar o bot automaticamente num horário do dia', type: 'checkbox' },
      { key: 'botPauseStart', label: 'Início da pausa', type: 'time' },
      { key: 'botPauseEnd', label: 'Fim da pausa', type: 'time' },
      { key: 'botPauseOutsideOpeningHours', label: 'Pausar fora do horário de funcionamento', type: 'checkbox' },
      { key: 'welcomeMessage', label: 'Mensagem de boas-vindas (antes do menu)', type: 'textarea', max: 600 },
      { key: 'holidayNoticeMessage', label: 'Aviso de feriado/férias', type: 'textarea', max: 600 },
      { key: 'holidayNoticeUntil', label: 'Mostrar aviso até', type: 'date' },
      { key: 'address', label: 'Endereço da loja', max: 200 },
      { key: 'mapLink', label: 'Link do mapa', placeholder: 'https://maps.google.com/…' },
      { key: 'reminder24hEnabled', label: 'Lembrete 24h antes com confirmação', type: 'checkbox' },
      { key: 'reminder24hTemplate', label: 'Texto do lembrete 24h ({Nome}, {Data}, {Hora}, {Barbeiro}, {Servico})', type: 'textarea' },
      { key: 'reminderResendHours', label: 'Reenviar se não confirmar em (horas, 0 = não)', type: 'number', min: 0, max: 24 },
      { key: 'minCancelHours', label: 'Antecedência mínima para cancelar/remarcar pelo bot (horas)', type: 'number', min: 0, max: 72 },
      { key: 'antiSpamMaxPerMinute', label: 'Anti-spam: máximo de mensagens por minuto', type: 'number', min: 3, max: 60 },
      { key: 'monthlyReportEnabled', label: 'Enviar resumo mensal ao dono no dia 1', type: 'checkbox' },
      { key: 'ownerPhone', label: 'WhatsApp do dono (resumos e alertas)' },
      { key: 'monthlyRevenueGoal', label: 'Meta de faturamento mensal (R$)', type: 'number', step: '0.01', min: 0 },
      { key: 'monthlyAppointmentsGoal', label: 'Meta de atendimentos no mês', type: 'number', min: 0 }
    ];
    get('/api/dashboard/bot/settings').then(s => {
      const box = $('[data-x="settings"]', root);
      box.innerHTML = form(settingsFields, s) + '<button type="button" class="btn btn-primary" data-save-settings>Salvar automação</button>';
      $('[data-save-settings]', box).addEventListener('click', guard(async () => {
        await send('PUT', '/api/dashboard/bot/settings', read(box, settingsFields));
        toast('Automação do bot salva.');
      }));
    }).catch(err => { $('[data-x="settings"]', root).textContent = err.message; });
  });

  async function openConversation(phone, refresh) {
    const d = await get(`/api/dashboard/bot/conversations/${encodeURIComponent(phone)}`);
    const s = d.summary;
    const hist = (d.history || []).slice(-40).map(m => `<div class="ext-msg ext-msg-${m.direction === 'outbound' ? 'out' : 'in'}">${h(m.text || '')}${m.selectedOption ? ` <small>→ ${h(m.selectedOption)}</small>` : ''}</div>`).join('');
    const dlg = dialog(`Conversa — ${s.customerName || s.phone}`, `
      <div class="ext-chat">${hist || '<p class="ext-hint">Sem histórico.</p>'}</div>
      <div class="field"><label>Responder como equipe</label><textarea rows="3" data-reply maxlength="1500"></textarea></div>
      <div class="ext-toolbar">
        <button type="button" class="btn btn-primary" data-send>Enviar</button>
        <button type="button" class="btn btn-warning" data-take>${s.humanTakeover ? 'Devolver ao bot' : 'Assumir conversa (pausa o bot só aqui)'}</button>
        <button type="button" class="btn btn-success" data-resolve>Marcar como resolvida</button>
      </div>`, { wide: true });
    $('[data-send]', dlg).addEventListener('click', guard(async () => {
      const text = $('[data-reply]', dlg).value.trim();
      if (!text) return;
      await send('POST', `/api/dashboard/bot/conversations/${encodeURIComponent(phone)}/reply`, { text });
      toast('Mensagem enviada.'); dlg.close(); refresh?.();
    }));
    $('[data-take]', dlg).addEventListener('click', guard(async () => {
      await send('POST', `/api/dashboard/bot/conversations/${encodeURIComponent(phone)}/takeover`, { enabled: !s.humanTakeover, hours: 4 });
      toast(s.humanTakeover ? 'Conversa devolvida ao bot.' : 'Você assumiu a conversa por até 4 horas.'); dlg.close(); refresh?.();
    }));
    $('[data-resolve]', dlg).addEventListener('click', guard(async () => {
      await send('POST', `/api/dashboard/bot/conversations/${encodeURIComponent(phone)}/resolve`);
      toast('Conversa resolvida.'); dlg.close(); refresh?.();
    }));
  }

  /* ================================================================== AGENDA (25–34, 95) */
  addSubtab('section-agenda', 'board', 'Quadro', async (root) => {
    root.innerHTML = `<div class="ext-toolbar"><input type="date" data-x="date" value="${todayIso()}"><button type="button" class="btn btn-secondary" data-x="reload">Atualizar</button>
      <button type="button" class="btn btn-secondary" data-x="reception" aria-pressed="false">Modo recepção</button>
      <span class="ext-hint">Arraste um cartão para outro horário ou profissional para remarcar.</span></div><div data-x="board" class="ext-board"></div>`;
    const dateEl = $('[data-x="date"]', root);
    const load = guard(async () => {
      const b = await get(`/api/dashboard/agenda/board?date=${dateEl.value}`);
      const board = $('[data-x="board"]', root);
      if (!b.barbers.length) { board.innerHTML = empty('Nenhum profissional ativo. Cadastre em Loja › Funcionários.'); return; }
      board.style.setProperty('--cols', b.barbers.length);
      board.innerHTML = `<div class="ext-board-head"><div></div>${b.barbers.map(c => `<div><strong>${h(c.name)}</strong><br><small>${c.occupancyPercent}% ocupado</small></div>`).join('')}</div>
        ${b.slots.map(slot => `<div class="ext-board-row"><div class="ext-board-time">${slot}</div>${b.barbers.map(c => {
          const cards = c.appointments.filter(a => a.startTime === slot);
          return `<div class="ext-board-cell" data-drop-barber="${c.id}" data-drop-time="${slot}">${cards.map(a => `
            <div class="ext-card ext-stage-${h(String(a.stage).toLowerCase())}" draggable="true" data-appt="${a.id}" style="--c:${h(a.colorHex)}">
              <strong>${h(a.customerName)}</strong> <small>${h(a.startTime)}–${h(a.endTime)}</small><br><small>${h(a.serviceName)} · ${money(a.price)}</small>
              ${a.customerConfirmed ? '<span class="ext-pill ext-pill-active">Confirmou</span>' : ''}
              <div class="ext-card-actions">${a.status === 'Completed' ? '<span class="ext-pill ext-pill-done">Concluído</span>' : `
                <button type="button" data-stage="arrived" data-id="${a.id}" title="Cliente chegou">Chegou</button>
                <button type="button" data-stage="start" data-id="${a.id}" title="Em atendimento">Iniciar</button>
                <button type="button" data-stage="complete" data-id="${a.id}" title="Concluir e registrar pagamento">Concluir</button>
                <button type="button" data-stage="noshow" data-id="${a.id}" title="Marcar falta">Faltou</button>`}</div>
            </div>`).join('')}</div>`;
        }).join('')}</div>`).join('')}`;
    });
    dateEl.addEventListener('change', load);
    $('[data-x="reload"]', root).addEventListener('click', load);
    $('[data-x="reception"]', root).addEventListener('click', (e) => {
      const on = document.body.classList.toggle('ext-reception');
      e.currentTarget.setAttribute('aria-pressed', String(on));
      clearInterval(root._timer);
      if (on) root._timer = setInterval(load, 30000);
    });
    root.addEventListener('click', guard(async (e) => {
      const b = e.target.closest('[data-stage]');
      if (!b) return;
      let paymentMethod = null;
      if (b.dataset.stage === 'complete') {
        paymentMethod = window.prompt('Forma de pagamento (PIX, Cartão, Dinheiro ou Assinatura):', 'PIX');
        if (paymentMethod === null) return;
      }
      await send('POST', `/api/dashboard/agenda/appointments/${b.dataset.id}/stage`, { stage: b.dataset.stage, paymentMethod });
      load();
    }));
    root.addEventListener('dragstart', (e) => { const c = e.target.closest('[data-appt]'); if (c) e.dataTransfer.setData('text/plain', c.dataset.appt); });
    root.addEventListener('dragover', (e) => { if (e.target.closest('[data-drop-time]')) e.preventDefault(); });
    root.addEventListener('drop', guard(async (e) => {
      const cell = e.target.closest('[data-drop-time]');
      const id = e.dataTransfer.getData('text/plain');
      if (!cell || !id) return;
      e.preventDefault();
      await send('POST', `/api/dashboard/agenda/appointments/${id}/move`, { date: dateEl.value, time: cell.dataset.dropTime, barberId: Number(cell.dataset.dropBarber), notifyCustomer: true });
      toast('Agendamento remarcado (cliente avisado no WhatsApp).');
      load();
    }));
    load();
  });

  addSubtab('section-agenda', 'blocks', 'Bloqueios', async (root) => {
    const bs = await barbers();
    const fields = [
      { key: 'barberId', label: 'Profissional', type: 'select', numeric: true, options: [{ value: 0, label: 'Todos' }, ...bs.map(b => ({ value: b.id, label: b.name }))] },
      { key: 'dayOfWeek', label: 'Dia da semana', type: 'select', numeric: true, options: dows.map((d, i) => ({ value: i, label: d })) },
      { key: 'startTime', label: 'Início', type: 'time', def: '12:00' },
      { key: 'endTime', label: 'Fim', type: 'time', def: '13:00' },
      { key: 'reason', label: 'Motivo', def: 'Bloqueio' }
    ];
    root.innerHTML = `<div class="surface-card"><h3>Bloqueios recorrentes</h3><p class="ext-hint">Horários que se repetem toda semana (reunião, curso, pausa). O bot não oferece esses horários.</p>
      <div data-x="list"></div></div><div class="surface-card"><h3>Novo bloqueio</h3>${form(fields)}<button type="button" class="btn btn-primary" data-x="add">Adicionar</button></div>
      <div class="surface-card"><h3>Agenda externa (Google Agenda)</h3><p class="ext-hint">Cole o "Endereço secreto em formato iCal" da agenda do profissional. Compromissos dele viram horários ocupados (atualiza a cada 10 min, somente leitura).</p><div data-x="cal"></div></div>`;
    const load = guard(async () => {
      const list = await get('/api/dashboard/agenda/blocks');
      $('[data-x="list"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(b => `<li class="ext-row"><div><strong>${dows[b.dayOfWeek]}</strong> ${h(b.startTime)}–${h(b.endTime)} · ${h(b.barberName)}<br><small>${h(b.reason)}</small></div>
        <button type="button" class="btn btn-danger btn-compact" data-del="${b.id}">Remover</button></li>`).join('')}</ul>` : empty('Nenhum bloqueio recorrente.');
      const cals = await get('/api/dashboard/extensions/calendars');
      $('[data-x="cal"]', root).innerHTML = `<ul class="ext-list">${cals.map(c => `<li class="ext-row"><div><strong>${h(c.barberName)}</strong><br><small>${c.hasCalendar ? `Conectada · ${c.events} compromisso(s) · ${c.lastSync ? 'lida ' + new Date(c.lastSync).toLocaleTimeString('pt-BR') : 'aguardando 1ª leitura'}` : 'Sem agenda externa'}</small></div>
        <div><button type="button" class="btn btn-secondary btn-compact" data-cal="${c.barberId}">${c.hasCalendar ? 'Trocar' : 'Conectar'}</button>${c.hasCalendar ? `<button type="button" class="btn btn-danger btn-compact" data-cal-off="${c.barberId}">Desconectar</button>` : ''}</div></li>`).join('')}</ul>`;
    });
    $('[data-x="add"]', root).addEventListener('click', guard(async () => {
      await send('POST', '/api/dashboard/agenda/blocks', read(root, fields)); toast('Bloqueio criado.'); load();
    }));
    root.addEventListener('click', guard(async (e) => {
      const del = e.target.closest('[data-del]');
      if (del) undoable('Bloqueio será removido.', async () => { await send('DELETE', `/api/dashboard/agenda/blocks/${del.dataset.del}`); load(); });
      const cal = e.target.closest('[data-cal]');
      if (cal) {
        const url = window.prompt('Endereço secreto iCal (https://calendar.google.com/…/basic.ics):');
        if (url) { await send('PUT', `/api/dashboard/extensions/calendars/${cal.dataset.cal}`, { icsUrl: url }); toast('Agenda conectada. A primeira leitura ocorre em até 10 minutos.'); load(); }
      }
      const off = e.target.closest('[data-cal-off]');
      if (off) { await send('PUT', `/api/dashboard/extensions/calendars/${off.dataset.calOff}`, { icsUrl: null }); load(); }
    }));
    load();
  });

  addSubtab('section-agenda', 'waitlist', 'Lista de espera', async (root) => {
    const [bs, ss] = await Promise.all([barbers(), services()]);
    const fields = [
      { key: 'customerName', label: 'Nome', required: true },
      { key: 'phone', label: 'WhatsApp', placeholder: '(11) 99999-9999' },
      { key: 'date', label: 'Dia desejado', type: 'date', def: todayIso() },
      { key: 'barberId', label: 'Profissional', type: 'select', numeric: true, options: [{ value: '', label: 'Qualquer' }, ...bs.map(b => ({ value: b.id, label: b.name }))] },
      { key: 'serviceItemId', label: 'Serviço', type: 'select', numeric: true, options: [{ value: '', label: '—' }, ...ss.map(s => ({ value: s.id, label: s.name }))] },
      { key: 'notes', label: 'Observação' }
    ];
    root.innerHTML = `<div class="surface-card"><h3>Lista de espera</h3><p class="ext-hint">Quando um horário vaga (cancelamento ou falta), quem está aqui é avisado pelo WhatsApp.</p>
      <div class="ext-toolbar"><input type="date" data-x="date" value="${todayIso()}"><button type="button" class="btn btn-secondary" data-x="all">Todos os dias</button></div><div data-x="list"></div></div>
      <div class="surface-card"><h3>Adicionar à espera</h3>${form(fields)}<button type="button" class="btn btn-primary" data-x="add">Adicionar</button></div>`;
    let filter = todayIso();
    const load = guard(async () => {
      const list = await get(`/api/dashboard/agenda/waitlist${filter ? `?date=${filter}` : ''}`);
      $('[data-x="list"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(w => `<li class="ext-row"><div><strong>${h(w.customerName)}</strong> <small>${h(w.phone)}</small><br>
        <small>${dateBR(w.date)} · ${h(w.barberName || 'Qualquer')} · ${h(w.serviceName || '')} ${h(w.notes || '')}</small></div>
        <div>${pill(w.status)}<button type="button" class="btn btn-success btn-compact" data-ws="${w.id}" data-st="Booked">Agendado</button>
        <button type="button" class="btn btn-danger btn-compact" data-wdel="${w.id}">Remover</button></div></li>`).join('')}</ul>` : empty('Ninguém na lista de espera.');
    });
    $('[data-x="date"]', root).addEventListener('change', (e) => { filter = e.target.value; load(); });
    $('[data-x="all"]', root).addEventListener('click', () => { filter = ''; load(); });
    $('[data-x="add"]', root).addEventListener('click', guard(async () => { await send('POST', '/api/dashboard/agenda/waitlist', read(root, fields)); toast('Adicionado à lista de espera.'); load(); }));
    root.addEventListener('click', guard(async (e) => {
      const st = e.target.closest('[data-ws]');
      if (st) { await send('PATCH', `/api/dashboard/agenda/waitlist/${st.dataset.ws}`, { status: st.dataset.st }); load(); }
      const del = e.target.closest('[data-wdel]');
      if (del) undoable('Registro será removido da espera.', async () => { await send('DELETE', `/api/dashboard/agenda/waitlist/${del.dataset.wdel}`); load(); });
    }));
    load();
  });

  /* ================================================================== CRM (35–44, 97) */
  async function openProfile(customerId) {
    const p = await get(`/api/dashboard/crm/customers/${customerId}/profile`);
    let loyalty = null;
    try { loyalty = await get(`/api/dashboard/crm/customers/${customerId}/loyalty`); } catch { /* fidelidade opcional */ }
    const fields = [
      { key: 'name', label: 'Nome' },
      { key: 'birthDate', label: 'Data de nascimento', type: 'date' },
      { key: 'marketingConsent', label: 'Aceita mensagens promocionais?', type: 'select', options: [{ value: 'Unknown', label: 'Não informado' }, { value: 'OptIn', label: 'Sim' }, { value: 'OptOut', label: 'Não (SAIR)' }] },
      { key: 'internalNotes', label: 'Observações internas (só a equipe vê)', type: 'textarea', max: 1000 }
    ];
    const dlg = dialog(`Cliente — ${p.name}`, `
      <div class="kpi-grid ext-kpis">
        <div class="kpi-card"><div class="kpi-label">Atendimentos</div><div class="kpi-value">${p.totalAppointments}</div></div>
        <div class="kpi-card"><div class="kpi-label">Total gasto</div><div class="kpi-value">${money(p.totalSpent)}</div></div>
        <div class="kpi-card"><div class="kpi-label">Ticket médio</div><div class="kpi-value">${money(p.averageTicket)}</div></div>
        <div class="kpi-card"><div class="kpi-label">Nota média</div><div class="kpi-value">${p.averageRating ? p.averageRating.toFixed(1) + ' ★' : '—'}</div></div>
      </div>
      <p class="ext-hint">${h(p.phone)} · 1ª visita ${dateBR(p.firstVisit) || '—'} · última ${dateBR(p.lastVisit) || '—'} · prefere ${h(p.favoriteService || '—')} com ${h(p.favoriteBarber || '—')} · ${p.cancellations} cancelamento(s), ${p.noShows} falta(s)</p>
      ${p.subscription ? `<p><strong>Assinatura:</strong> ${h(p.subscription.planName)} ${pill(p.subscription.status)} · até ${dateBR(p.subscription.currentPeriodEnd)}</p>` : ''}
      ${loyalty ? `<p><strong>Fidelidade:</strong> ${loyalty.points} pontos (prêmio com ${loyalty.rewardPoints}: ${h(loyalty.rewardDescription)}) <button type="button" class="btn btn-success btn-compact" data-redeem ${loyalty.points < loyalty.rewardPoints ? 'disabled' : ''}>Resgatar prêmio</button></p>` : ''}
      ${form(fields, p)}
      <details><summary>Histórico (${p.history.length})</summary><ul class="ext-list">${p.history.map(x => `<li>${dateBR(x.date)} ${h(x.time)} · ${h(x.service)} · ${h(x.barber)} · ${money(x.price)} · ${h(x.status)}${x.rating ? ' · ' + x.rating + '★' : ''}</li>`).join('')}</ul></details>`,
    { wide: true, footer: '<button type="button" class="btn btn-danger" data-anon>Anonimizar (LGPD)</button><button type="button" class="btn btn-primary" data-save>Salvar</button>' });
    $('[data-save]', dlg).addEventListener('click', guard(async () => {
      await send('PUT', `/api/dashboard/crm/customers/${customerId}`, read(dlg, fields)); toast('Cliente atualizado.'); dlg.close();
    }));
    $('[data-redeem]', dlg)?.addEventListener('click', guard(async () => {
      await send('POST', `/api/dashboard/crm/customers/${customerId}/loyalty/redeem`); toast('Prêmio resgatado.'); dlg.close();
    }));
    $('[data-anon]', dlg).addEventListener('click', guard(async () => {
      const pwd = window.prompt('Anonimizar apaga nome, telefone e observações deste cliente de forma irreversível. Confirme sua senha:');
      if (!pwd) return;
      const r = await send('POST', '/api/auth/reauth', { password: pwd });
      await send('POST', `/api/dashboard/crm/customers/${customerId}/anonymize`, { ticket: r.ticket });
      toast('Cliente anonimizado.'); dlg.close();
    }));
  }
  window.CHOpenProfile = (id) => guard(openProfile)(id);

  // Botão "Limpar" da aba CRM: zera filtros e formulários da sub-aba aberta e recarrega a lista.
  // Não apaga nenhum dado de cliente.
  (function addCrmClearButton() {
    const section = document.getElementById('section-crm');
    const zone = section?.querySelector('.subtab-header-zone');
    if (!section || !zone || document.getElementById('btn-crm-clear')) return;
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.id = 'btn-crm-clear';
    btn.className = 'btn btn-secondary';
    btn.style.cssText = 'min-height: 36px; padding: 4px 12px; margin-left: auto;';
    btn.title = 'Limpa filtros e formulários desta aba';
    btn.textContent = 'Limpar';
    btn.addEventListener('click', () => {
      const active = section.querySelector('.subtab-btn[role="tab"][aria-selected="true"]');
      const panel = active && document.getElementById(active.dataset.subpanel);
      if (panel) {
        panel.querySelectorAll('input, select, textarea').forEach(el => {
          if (el.type === 'checkbox' || el.type === 'radio') el.checked = el.defaultChecked;
          else el.value = el.defaultValue;
        });
      }
      if (active && loaders[active.dataset.subpanel]) loaders[active.dataset.subpanel]();
      toast('Aba limpa.');
    });
    zone.appendChild(btn);
  })();


  addSubtab('section-crm', 'inactive', 'Inativos', async (root) => {
    root.innerHTML = `<div class="ext-toolbar"><label>Sem vir há <input type="number" data-x="days" value="60" min="15" max="365" style="width:80px"> dias</label><button type="button" class="btn btn-secondary" data-x="go">Buscar</button></div><div data-x="list"></div>`;
    const load = guard(async () => {
      const list = await get(`/api/dashboard/crm/inactive?days=${$('[data-x="days"]', root).value}`);
      $('[data-x="list"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(c => `<li class="ext-row"><div><strong>${h(c.name)}</strong> <small>${h(c.phone)}</small><br><small>Última visita ${dateBR(c.lastVisit)} (${c.daysSince} dias) · ${c.totalAppointments} atendimento(s)</small></div>
        <button type="button" class="btn btn-secondary btn-compact" data-profile="${c.id}">Perfil</button></li>`).join('')}</ul>` : empty('Nenhum cliente inativo nesse período. 👏');
    });
    $('[data-x="go"]', root).addEventListener('click', load);
    load();
  });

  document.addEventListener('click', (e) => { const p = e.target.closest('[data-profile]'); if (p) window.CHOpenProfile(Number(p.dataset.profile)); });

  addSubtab('section-crm', 'campaigns', 'Campanhas', async (root) => {
    const ss = await services();
    const seg = [
      { key: 'minVisits', label: 'Mínimo de visitas', type: 'number', nullable: true },
      { key: 'maxVisits', label: 'Máximo de visitas', type: 'number', nullable: true },
      { key: 'inactiveDays', label: 'Sem vir há pelo menos (dias)', type: 'number', nullable: true },
      { key: 'activeWithinDays', label: 'Veio nos últimos (dias)', type: 'number', nullable: true },
      { key: 'serviceId', label: 'Já fez o serviço', type: 'select', numeric: true, options: [{ value: '', label: 'Qualquer' }, ...ss.map(s => ({ value: s.id, label: s.name }))] },
      { key: 'minAverageTicket', label: 'Ticket médio mínimo (R$)', type: 'number', nullable: true, step: '0.01' },
      { key: 'onlyOptIn', label: 'Somente quem aceitou promoções', type: 'checkbox' }
    ];
    const msg = [{ key: 'name', label: 'Nome da campanha', required: true }, { key: 'message', label: 'Mensagem ({Nome} e {Loja}; o rodapé "SAIR" é adicionado automaticamente)', type: 'textarea', rows: 4, max: 1000 }];
    root.innerHTML = `<div class="surface-card"><h3>Nova campanha</h3>${form(seg)}<button type="button" class="btn btn-secondary" data-x="preview">Ver quem vai receber</button><div data-x="pv"></div>
      ${form(msg)}<button type="button" class="btn btn-primary" data-x="create">Criar e enfileirar envio</button><p class="ext-hint">Envio gradual (~3 por minuto) entre 9h e 20h, só com o WhatsApp conectado.</p></div>
      <div class="surface-card"><h3>Campanhas</h3><div data-x="list"></div></div>`;
    $('[data-x="preview"]', root).addEventListener('click', guard(async () => {
      const r = await send('POST', '/api/dashboard/crm/segments/preview', read(root, seg));
      $('[data-x="pv"]', root).innerHTML = `<p><strong>${r.total}</strong> cliente(s) receberão (${r.excludedOptOut} excluído(s) por SAIR).</p><small>${r.sample.map(s => h(s.name)).join(', ')}</small>`;
    }));
    const load = guard(async () => {
      const list = await get('/api/dashboard/crm/campaigns');
      $('[data-x="list"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(c => `<li class="ext-row"><div><strong>${h(c.name)}</strong> ${pill(c.status)}<br><small>${c.sentCount}/${c.totalRecipients} enviadas · ${c.failedCount} falha(s) · ${new Date(c.createdAt).toLocaleDateString('pt-BR')}</small></div>
        ${c.status === 'Queued' || c.status === 'Sending' ? `<button type="button" class="btn btn-danger btn-compact" data-cancel="${c.id}">Cancelar</button>` : ''}</li>`).join('')}</ul>` : empty('Nenhuma campanha ainda.');
    });
    $('[data-x="create"]', root).addEventListener('click', guard(async () => {
      const body = { ...read(root, msg), segment: read(root, seg) };
      await send('POST', '/api/dashboard/crm/campaigns', body); toast('Campanha enfileirada.'); load();
    }));
    root.addEventListener('click', guard(async (e) => {
      const c = e.target.closest('[data-cancel]');
      if (c) { await send('POST', `/api/dashboard/crm/campaigns/${c.dataset.cancel}/cancel`); load(); }
    }));
    load();
  });

  addSubtab('section-crm', 'dupes', 'Duplicados', async (root) => {
    const load = guard(async () => {
      const groups = await get('/api/dashboard/crm/duplicates');
      root.innerHTML = `<p class="ext-hint">Clientes com o mesmo telefone ou nome muito parecido. Escolha qual manter; o histórico dos outros é unificado nele.</p>` + (groups.length ? groups.map((g, gi) => `
        <div class="surface-card"><ul class="ext-list">${g.customers.map((c, i) => `<li class="ext-row"><label><input type="radio" name="keep-${gi}" value="${c.id}" ${i === 0 ? 'checked' : ''}> <strong>${h(c.name)}</strong> <small>${h(c.phone)} · ${c.totalAppointments} atendimento(s) · ${dateBR(c.lastVisit) || '—'}</small></label></li>`).join('')}</ul>
        <button type="button" class="btn btn-primary btn-compact" data-merge="${gi}">Unificar</button></div>`).join('') : empty('Nenhum cadastro duplicado encontrado.'));
      root._groups = groups;
    });
    root.addEventListener('click', guard(async (e) => {
      const b = e.target.closest('[data-merge]');
      if (!b) return;
      const g = root._groups[Number(b.dataset.merge)];
      const keep = Number($(`input[name="keep-${b.dataset.merge}"]:checked`, root).value);
      await send('POST', '/api/dashboard/crm/merge', { keepId: keep, mergeIds: g.customers.map(c => c.id).filter(id => id !== keep) });
      toast('Cadastros unificados.'); load();
    }));
    load();
  });

  addSubtab('section-crm', 'trend', 'Notas', async (root) => {
    const t = await get('/api/dashboard/crm/evaluations/trend');
    const bar = (avg) => avg ? `<span class="ext-bar" style="--w:${(avg / 5) * 100}%"></span> ${avg.toFixed(2)}★` : '—';
    root.innerHTML = `<div class="surface-card"><h3>Nota média geral: ${t.overallAverage ? t.overallAverage.toFixed(2) + ' ★' : '—'} <small>(${t.totalRatings} avaliações)</small></h3>
      <ul class="ext-list">${t.monthly.map(m => `<li class="ext-row"><span>${h(m.month)}</span><span class="ext-bar-wrap">${bar(m.average)} <small>(${m.count})</small></span></li>`).join('')}</ul></div>
      <div class="surface-card"><h3>Por profissional</h3><ul class="ext-list">${t.byBarber.map(b => `<li class="ext-row"><strong>${h(b.barberName)}</strong><span class="ext-bar-wrap">${bar(b.average)} <small>(${b.count})</small></span></li>`).join('')}</ul></div>`;
  });

  /* ================================================================== RELATÓRIOS (45–54, 64) */
  addBlock('section-relatorios', 'overview', async (root) => {
    const first = todayIso().slice(0, 8) + '01';
    if (!root.dataset.ready) {
      root.dataset.ready = '1';
      root.innerHTML = `<div class="surface-card"><div class="ext-toolbar"><strong>Período</strong><input type="date" data-x="from" value="${first}"><input type="date" data-x="to" value="${todayIso()}">
        <button type="button" class="btn btn-secondary" data-x="go">Comparar com período anterior</button><a class="btn btn-success" data-x="export" href="#">Exportar período (.csv)</a></div><div data-x="ov"></div></div>
        <div class="ext-grid-2"><div class="surface-card"><h3>Fechamento de caixa</h3><input type="date" data-x="cash-date" value="${todayIso()}"><div data-x="cash"></div></div>
        <div class="surface-card"><h3>Assinaturas</h3><div data-x="subs"></div></div></div>`;
      $('[data-x="go"]', root).addEventListener('click', () => loadOverview(root));
      $('[data-x="cash-date"]', root).addEventListener('change', () => loadCash(root));
    }
    loadOverview(root); loadCash(root); loadSubsReport(root);
  });

  const loadOverview = guard(async (root) => {
    const from = $('[data-x="from"]', root).value, to = $('[data-x="to"]', root).value;
    $('[data-x="export"]', root).setAttribute('href', `/api/dashboard/reports/export/period?from=${from}&to=${to}`);
    const o = await get(`/api/dashboard/reports/overview?from=${from}&to=${to}`);
    const fmt = (k) => k.format === 'currency' ? money(k.value) : k.format === 'percent' ? `${Number(k.value).toFixed(1)}%` : k.format === 'rating' ? (Number(k.value) ? `${Number(k.value).toFixed(2)} ★` : '—') : Number(k.value).toLocaleString('pt-BR');
    const delta = (k) => {
      if (k.deltaPercent == null) return '<small class="ext-delta">—</small>';
      const good = (k.deltaPercent >= 0) === k.higherIsBetter;
      return `<small class="ext-delta ${good ? 'up' : 'down'}">${k.deltaPercent >= 0 ? '▲' : '▼'} ${Math.abs(k.deltaPercent).toFixed(1)}%</small>`;
    };
    const g = o.goals;
    const pct = (a, b) => b > 0 ? Math.min(100, Math.round(a * 100 / b)) : 0;
    const maxWeek = Math.max(1, ...o.weeklyRevenue.map(w => w.revenue));
    $('[data-x="ov"]', root).innerHTML = `
      <p class="ext-hint">${dateBR(o.from)} a ${dateBR(o.to)} comparado com ${dateBR(o.previousFrom)} a ${dateBR(o.previousTo)}</p>
      <div class="kpi-grid">${o.kpis.map(k => `<div class="kpi-card"><div class="kpi-label">${h(k.label)}</div><div class="kpi-value">${fmt(k)}</div>${delta(k)}</div>`).join('')}</div>
      <div class="ext-grid-2">
        <div><h4>Hoje / semana / mês</h4><p>Hoje: <strong>${money(o.owner.todayRevenue)}</strong> (${o.owner.todayCount}) · Semana: <strong>${money(o.owner.weekRevenue)}</strong> (${o.owner.weekCount}) · Mês: <strong>${money(o.owner.monthRevenue)}</strong> (${o.owner.monthCount})</p>
          <h4>Metas de ${h(g.month)}</h4>
          ${g.revenueGoal > 0 ? `<p>Faturamento ${money(g.revenueActual)} de ${money(g.revenueGoal)}<span class="ext-progress"><span style="width:${pct(g.revenueActual, g.revenueGoal)}%"></span></span></p>` : '<p class="ext-hint">Defina metas em WhatsApp › Automação.</p>'}
          ${g.appointmentsGoal > 0 ? `<p>Atendimentos ${g.appointmentsActual} de ${g.appointmentsGoal}<span class="ext-progress"><span style="width:${pct(g.appointmentsActual, g.appointmentsGoal)}%"></span></span></p>` : ''}</div>
        <div><h4>Faturamento por semana</h4>${o.weeklyRevenue.map(w => `<div class="ext-row"><small>${dateBR(w.weekStart)}</small><span class="ext-bar-wrap"><span class="ext-bar" style="--w:${(w.revenue / maxWeek) * 100}%"></span> ${money(w.revenue)}</span></div>`).join('') || '<p class="ext-hint">Sem dados.</p>'}</div>
      </div>
      <div class="ext-grid-2">
        <div><h4>Ticket por profissional</h4>${table(['Profissional', 'Receita', 'Qtd', 'Ticket'], o.byBarber.map(b => [b.name, money(b.revenue), b.count, money(b.averageTicket)]))}</div>
        <div><h4>Ocupação</h4>${table(['Profissional', 'Ocupação'], o.occupancyByBarber.map(b => [b.name, `${b.percent}%`]))}</div>
        <div><h4>Por serviço</h4>${table(['Serviço', 'Receita', 'Qtd'], o.byService.map(b => [b.name, money(b.revenue), b.count]))}</div>
        <div><h4>Tempo real × configurado</h4>${table(['Serviço', 'Configurado', 'Média real'], o.serviceTimes.map(s => [s.serviceName, `${s.configuredMinutes} min`, s.averageMinutes ? `${s.averageMinutes} min (${s.samples})` : '—']))}</div>
      </div>`;
  });

  function table(head, rows) {
    if (!rows.length) return '<p class="ext-hint">Sem dados no período.</p>';
    return `<div class="ext-table-wrap"><table class="ext-table"><thead><tr>${head.map(x => `<th>${h(x)}</th>`).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.map(c => `<td>${h(c)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`;
  }

  const loadCash = guard(async (root) => {
    const date = $('[data-x="cash-date"]', root).value;
    const c = await get(`/api/dashboard/reports/cash?date=${date}`);
    const fields = [
      { key: 'countedPix', label: `PIX conferido (esperado ${money(c.expectedPix)})`, type: 'number', step: '0.01' },
      { key: 'countedCard', label: `Cartão conferido (esperado ${money(c.expectedCard)})`, type: 'number', step: '0.01' },
      { key: 'countedCash', label: `Dinheiro conferido (esperado ${money(c.expectedCash)})`, type: 'number', step: '0.01' },
      { key: 'notes', label: 'Observações' }
    ];
    const box = $('[data-x="cash"]', root);
    box.innerHTML = `<p class="ext-hint">${c.completedCount} atendimento(s) concluído(s). ${c.closedAt ? `Fechado por ${h(c.closedBy)} em ${new Date(c.closedAt).toLocaleString('pt-BR')}.` : 'Caixa ainda não fechado.'}</p>
      ${form(fields, { countedPix: c.countedPix ?? c.expectedPix, countedCard: c.countedCard ?? c.expectedCard, countedCash: c.countedCash ?? c.expectedCash, notes: c.notes })}
      <button type="button" class="btn btn-primary" data-close-cash>Fechar caixa</button>`;
    $('[data-close-cash]', box).addEventListener('click', guard(async () => {
      const r = await send('POST', '/api/dashboard/reports/cash', { date, ...read(box, fields) });
      const diff = (r.countedPix + r.countedCard + r.countedCash) - (r.expectedPix + r.expectedCard + r.expectedCash);
      toast(diff === 0 ? 'Caixa fechado e conferido.' : `Caixa fechado com diferença de ${money(diff)}.`);
      loadCash(root);
    }));
  });

  const loadSubsReport = guard(async (root) => {
    const r = await get('/api/dashboard/plans/report');
    $('[data-x="subs"]', root).innerHTML = `<div class="kpi-grid ext-kpis">
      <div class="kpi-card"><div class="kpi-label">Ativas</div><div class="kpi-value">${r.active}</div></div>
      <div class="kpi-card"><div class="kpi-label">Receita recorrente</div><div class="kpi-value">${money(r.monthlyRecurringRevenue)}</div></div>
      <div class="kpi-card"><div class="kpi-label">Cancelamento (30d)</div><div class="kpi-value">${r.churnRatePercent}%</div></div>
      <div class="kpi-card"><div class="kpi-label">Em atraso</div><div class="kpi-value">${r.overdue}</div></div></div>
      <p class="ext-hint">${r.newLast30Days} nova(s) e ${r.cancelledLast30Days} cancelada(s) em 30 dias · ${r.paused} pausada(s) · média de ${r.averageUsesPerActive} uso(s) por assinante.</p>
      ${table(['Plano', 'Ativos', 'Receita/mês'], r.byPlan.map(p => [p.planName, p.active, money(p.monthlyRevenue)]))}`;
  });

  /* ================================================================== LOJA: PLANOS (55–66) */
  addSubtab('section-loja', 'plans', 'Planos', async (root) => {
    root.innerHTML = `<div data-x="sum" class="kpi-grid"></div>
      <div class="surface-card"><div class="ext-toolbar"><h3 style="margin:0">Planos de assinatura</h3><button type="button" class="btn btn-primary" data-x="new-plan">Novo plano</button><span data-x="publink" class="ext-hint"></span></div><div data-x="plans"></div></div>
      <div class="surface-card"><div class="ext-toolbar"><h3 style="margin:0">Assinantes</h3><select data-x="st"><option value="all">Ativas, pausadas e em atraso</option><option value="Active">Ativas</option><option value="Overdue">Em atraso</option><option value="Paused">Pausadas</option><option value="Cancelled">Canceladas</option></select>
        <button type="button" class="btn btn-primary" data-x="new-sub">Assinar cliente</button></div><div data-x="subs"></div></div>`;
    const ss = await services();
    let plans = [];
    const planFields = () => [
      { key: 'name', label: 'Nome do plano', required: true, placeholder: 'Clube do Corte' },
      { key: 'description', label: 'Descrição', type: 'textarea', max: 400 },
      { key: 'price', label: 'Preço (R$)', type: 'number', step: '0.01', min: 1 },
      { key: 'includedServiceIds', label: 'Serviços inclusos (Ctrl para vários)', type: 'select', multiple: true, options: ss.map(s => ({ value: s.id, label: s.name })) },
      { key: 'usesPerMonth', label: 'Usos por período (0 = ilimitado)', type: 'number', min: 0, def: 4 },
      { key: 'validityDays', label: 'Duração do período (dias)', type: 'number', min: 7, def: 30 },
      { key: 'maxMembers', label: 'Pessoas no plano (2+ = família/dupla)', type: 'number', min: 1, max: 6, def: 1 },
      { key: 'priorityBookingDays', label: 'Dias extras de antecedência para agendar', type: 'number', min: 0, def: 7 },
      { key: 'isPublic', label: 'Mostrar na página pública', type: 'checkbox', def: true },
      { key: 'isActive', label: 'Ativo (à venda)', type: 'checkbox', def: true }
    ];
    const load = guard(async () => {
      const [sum, list, ext] = await Promise.all([get('/api/dashboard/plans/summary'), get('/api/dashboard/plans'), get('/api/dashboard/extensions/settings').catch(() => null)]);
      plans = list;
      $('[data-x="sum"]', root).innerHTML = `<div class="kpi-card"><div class="kpi-label">Assinantes ativos</div><div class="kpi-value">${sum.activeSubscribers}</div></div>
        <div class="kpi-card"><div class="kpi-label">Receita recorrente/mês</div><div class="kpi-value">${money(sum.monthlyRecurringRevenue)}</div></div>
        <div class="kpi-card"><div class="kpi-label">Vencem em 7 dias</div><div class="kpi-value">${sum.expiringIn7Days}</div></div>`;
      $('[data-x="publink"]', root).innerHTML = ext?.publicSlug ? `Página pública: <a href="/p/${h(ext.publicSlug)}" target="_blank" rel="noopener">${location.origin}/p/${h(ext.publicSlug)}</a>` : 'Configure a página pública em Loja › Recursos.';
      $('[data-x="plans"]', root).innerHTML = list.length ? `<div class="ext-cards">${list.map(p => `<div class="ext-plan ${p.isActive ? '' : 'is-off'}"><strong>${h(p.name)}</strong><div class="kpi-value">${money(p.price)}</div>
        <small>${p.usesPerMonth ? `${p.usesPerMonth} uso(s)` : 'Ilimitado'} a cada ${p.validityDays} dias · ${h(p.includedServiceNames.join(', '))}${p.maxMembers > 1 ? ` · até ${p.maxMembers} pessoas` : ''}${p.priorityBookingDays ? ` · +${p.priorityBookingDays} dias de prioridade` : ''}</small>
        <small>${p.activeSubscribers} assinante(s) ${p.isActive ? '' : '· inativo'}</small>
        <div class="ext-toolbar"><button type="button" class="btn btn-secondary btn-compact" data-edit-plan="${p.id}">Editar</button><button type="button" class="btn btn-danger btn-compact" data-del-plan="${p.id}">Excluir</button></div></div>`).join('')}</div>`
        : empty('Nenhum plano criado. Crie um "clube" de cortes para ter receita recorrente.', 'Criar primeiro plano', 'data-x="new-plan"');
      loadSubs();
    });
    const loadSubs = guard(async () => {
      const list = await get(`/api/dashboard/plans/subscriptions?status=${$('[data-x="st"]', root).value}`);
      $('[data-x="subs"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(s => `<li class="ext-row"><div><strong>${h(s.customerName)}</strong> ${pill(s.status)} <small>${h(s.customerPhone)}</small><br>
        <small>${h(s.planName)} · ${money(s.planPrice)} · até ${dateBR(s.currentPeriodEnd)} · uso ${s.usesThisPeriod}/${s.usesPerMonth || '∞'}${s.creditBalance > 0 ? ` · crédito ${money(s.creditBalance)}` : ''}${(s.members || []).length ? ` · membros: ${s.members.map(m => h(m.name)).join(', ')}` : ''}</small></div>
        <div class="ext-actions">${s.status !== 'Cancelled' ? `
          <button type="button" class="btn btn-success btn-compact" data-sub="renew" data-id="${s.id}">Registrar pagamento</button>
          <button type="button" class="btn btn-secondary btn-compact" data-sub="pix" data-id="${s.id}">PIX</button>
          <button type="button" class="btn btn-secondary btn-compact" data-sub="change" data-id="${s.id}">Trocar plano</button>
          ${s.maxMembers > 1 ? `<button type="button" class="btn btn-secondary btn-compact" data-sub="member" data-id="${s.id}">+ Membro</button>` : ''}
          <button type="button" class="btn btn-secondary btn-compact" data-sub="${s.status === 'Paused' ? 'Active' : 'Paused'}" data-id="${s.id}">${s.status === 'Paused' ? 'Reativar' : 'Pausar'}</button>
          <button type="button" class="btn btn-danger btn-compact" data-sub="Cancelled" data-id="${s.id}">Cancelar</button>` : ''}</div></li>`).join('')}</ul>` : empty('Nenhuma assinatura neste filtro.');
    });
    const editPlan = (p) => {
      const fs = planFields();
      const dlg = dialog(p ? 'Editar plano' : 'Novo plano', form(fs, p || {}), { footer: '<button type="button" class="btn btn-primary" data-save>Salvar plano</button>' });
      $('[data-save]', dlg).addEventListener('click', guard(async () => {
        await send(p ? 'PUT' : 'POST', p ? `/api/dashboard/plans/${p.id}` : '/api/dashboard/plans', read(dlg, fs));
        dlg.close(); toast('Plano salvo.'); load();
      }));
    };
    const pickCustomer = async (title) => {
      const q = window.prompt(`${title}\nDigite nome ou telefone do cliente:`);
      if (!q) return null;
      const hits = (await get(`/api/dashboard/search?q=${encodeURIComponent(q)}`)).filter(x => x.kind === 'cliente');
      if (!hits.length) { toast('Nenhum cliente encontrado.'); return null; }
      if (hits.length === 1) return Number(hits[0].id);
      const pick = window.prompt(hits.map((x, i) => `${i + 1}) ${x.title} — ${x.subtitle}`).join('\n') + '\n\nNúmero da opção:');
      const idx = Number(pick) - 1;
      return hits[idx] ? Number(hits[idx].id) : null;
    };
    root.addEventListener('click', guard(async (e) => {
      if (e.target.closest('[data-x="new-plan"]')) return editPlan(null);
      const ep = e.target.closest('[data-edit-plan]');
      if (ep) return editPlan(plans.find(p => p.id === Number(ep.dataset.editPlan)));
      const dp = e.target.closest('[data-del-plan]');
      if (dp) return undoable('Plano será excluído (ou desativado, se tiver assinantes).', async () => { await send('DELETE', `/api/dashboard/plans/${dp.dataset.delPlan}`); load(); });
      if (e.target.closest('[data-x="new-sub"]')) {
        const active = plans.filter(p => p.isActive);
        if (!active.length) { toast('Crie um plano ativo primeiro.'); return; }
        const customerId = await pickCustomer('Assinar cliente');
        if (!customerId) return;
        const choice = window.prompt(active.map((p, i) => `${i + 1}) ${p.name} — ${money(p.price)}`).join('\n') + '\n\nNúmero do plano:');
        const plan = active[Number(choice) - 1];
        if (!plan) return;
        await send('POST', '/api/dashboard/plans/subscriptions', { customerId, planId: plan.id, startDate: todayIso(), notes: null });
        toast('Assinatura criada.'); return load();
      }
      const b = e.target.closest('[data-sub]');
      if (!b) return;
      const id = b.dataset.id, act = b.dataset.sub;
      if (act === 'renew') { await send('POST', `/api/dashboard/plans/subscriptions/${id}/renew`); toast('Pagamento registrado e período renovado.'); return loadSubs(); }
      if (act === 'pix') {
        const pix = await get(`/api/dashboard/plans/subscriptions/${id}/pix`);
        const dlg = dialog('Cobrança PIX', `<p>${h(pix.description)} — <strong>${money(pix.amount)}</strong></p><textarea readonly rows="5" class="ext-mono">${h(pix.copyPaste)}</textarea>`, { footer: '<button type="button" class="btn btn-primary" data-copy>Copiar código</button>' });
        $('[data-copy]', dlg).addEventListener('click', () => navigator.clipboard?.writeText(pix.copyPaste).then(() => toast('Código PIX copiado.')));
        return;
      }
      if (act === 'change') {
        const others = plans.filter(p => p.isActive);
        const choice = window.prompt(others.map((p, i) => `${i + 1}) ${p.name} — ${money(p.price)}`).join('\n') + '\n\nNovo plano (número):');
        const plan = others[Number(choice) - 1];
        if (!plan) return;
        const r = await send('POST', `/api/dashboard/plans/subscriptions/${id}/change-plan`, { newPlanId: plan.id });
        toast(`Plano trocado. Crédito ${money(r.creditFromOldPlan)} · a pagar ${money(r.amountDue)}.`); return loadSubs();
      }
      if (act === 'member') {
        const customerId = await pickCustomer('Adicionar membro ao plano família');
        if (!customerId) return;
        await send('POST', `/api/dashboard/plans/subscriptions/${id}/members`, { customerId }); toast('Membro adicionado.'); return loadSubs();
      }
      if (act === 'Cancelled') return undoable('Assinatura será cancelada.', async () => { await send('POST', `/api/dashboard/plans/subscriptions/${id}/status`, { status: 'Cancelled' }); loadSubs(); });
      await send('POST', `/api/dashboard/plans/subscriptions/${id}/status`, { status: act }); loadSubs();
    }));
    $('[data-x="st"]', root).addEventListener('change', loadSubs);
    load();
  });

  /* ================================================================== LOJA: ESTOQUE (99) */
  addSubtab('section-loja', 'stock', 'Estoque', async (root) => {
    const fields = [
      { key: 'name', label: 'Produto', required: true }, { key: 'sku', label: 'Código (opcional)' },
      { key: 'minQuantity', label: 'Estoque mínimo (alerta)', type: 'number', step: '0.01', def: 2 },
      { key: 'unitCost', label: 'Custo unitário (R$)', type: 'number', step: '0.01' },
      { key: 'salePrice', label: 'Preço de venda (R$)', type: 'number', step: '0.01' },
      { key: 'initialQuantity', label: 'Quantidade inicial', type: 'number', step: '0.01', nullable: true },
      { key: 'isActive', label: 'Ativo', type: 'checkbox', def: true }
    ];
    root.innerHTML = `<div class="surface-card"><div class="ext-toolbar"><h3 style="margin:0">Estoque</h3><button type="button" class="btn btn-primary" data-x="new">Novo produto</button></div><div data-x="list"></div></div>
      <div class="surface-card"><h3>Últimas movimentações</h3><div data-x="moves"></div></div>`;
    let items = [];
    const load = guard(async () => {
      items = await get('/api/dashboard/stock');
      const low = items.filter(i => i.isLow);
      $('[data-x="list"]', root).innerHTML = (low.length ? `<p class="ext-alert">⚠️ Estoque baixo: ${low.map(i => h(i.name)).join(', ')}</p>` : '') + (items.length ? `<div class="ext-table-wrap"><table class="ext-table"><thead><tr><th>Produto</th><th>Qtd</th><th>Mínimo</th><th>Custo</th><th>Venda</th><th></th></tr></thead><tbody>${items.map(i => `<tr class="${i.isActive ? '' : 'is-off'}"><td>${h(i.name)} ${i.sku ? `<small>${h(i.sku)}</small>` : ''}</td><td class="${i.isLow ? 'ext-low' : ''}">${i.quantity}</td><td>${i.minQuantity}</td><td>${money(i.unitCost)}</td><td>${money(i.salePrice)}</td>
        <td class="ext-actions"><button type="button" class="btn btn-success btn-compact" data-mv="Entrada" data-id="${i.id}">+ Entrada</button><button type="button" class="btn btn-secondary btn-compact" data-mv="Venda" data-id="${i.id}">Venda</button><button type="button" class="btn btn-secondary btn-compact" data-mv="Uso" data-id="${i.id}">Uso</button><button type="button" class="btn btn-secondary btn-compact" data-edit="${i.id}">Editar</button></td></tr>`).join('')}</tbody></table></div>`
        : empty('Nenhum produto cadastrado.', 'Cadastrar produto', 'data-x="new"'));
      const moves = await get('/api/dashboard/stock/movements');
      $('[data-x="moves"]', root).innerHTML = table(['Quando', 'Produto', 'Qtd', 'Motivo', 'Quem'], moves.slice(0, 30).map(m => [new Date(m.createdAt).toLocaleString('pt-BR'), m.itemName, m.delta > 0 ? `+${m.delta}` : m.delta, m.reason, m.createdBy || '']));
    });
    const edit = (it) => {
      const fs = it ? fields.filter(f => f.key !== 'initialQuantity') : fields;
      const dlg = dialog(it ? 'Editar produto' : 'Novo produto', form(fs, it || {}), { footer: '<button type="button" class="btn btn-primary" data-save>Salvar</button>' });
      $('[data-save]', dlg).addEventListener('click', guard(async () => {
        await send(it ? 'PUT' : 'POST', it ? `/api/dashboard/stock/${it.id}` : '/api/dashboard/stock', read(dlg, fs)); dlg.close(); toast('Produto salvo.'); load();
      }));
    };
    root.addEventListener('click', guard(async (e) => {
      if (e.target.closest('[data-x="new"]')) return edit(null);
      const ed = e.target.closest('[data-edit]');
      if (ed) return edit(items.find(i => i.id === Number(ed.dataset.edit)));
      const mv = e.target.closest('[data-mv]');
      if (mv) {
        const q = window.prompt(`${mv.dataset.mv}: quantidade`, '1');
        if (!q) return;
        await send('POST', `/api/dashboard/stock/${mv.dataset.id}/move`, { delta: Number(q.replace(',', '.')), reason: mv.dataset.mv });
        load();
      }
    }));
    load();
  });

  /* ================================================================== LOJA: RECURSOS (59, 66, 94, 96–100, 68) */
  addSubtab('section-loja', 'ext', 'Recursos', async (root) => {
    const s = await get('/api/dashboard/extensions/settings');
    const fields = [
      { key: 'publicSlug', label: 'Endereço da página pública (ex.: clinic-hair)', hint: 'Fica em /p/endereço — mostra planos e permite agendar.' },
      { key: 'publicBookingEnabled', label: 'Permitir agendamento online pela página pública', type: 'checkbox' },
      { key: 'bookingHorizonDays', label: 'Agendamento online até quantos dias à frente', type: 'number', min: 1, max: 90 },
      { key: 'pixKey', label: 'Chave PIX da loja', hint: 'Usada nas cobranças de assinatura e no pagamento online (copia e cola).' },
      { key: 'pixMerchantName', label: 'Nome do recebedor (até 25 letras)' },
      { key: 'pixMerchantCity', label: 'Cidade do recebedor (até 15 letras)' },
      { key: 'onlinePixEnabled', label: 'Oferecer PIX ao agendar online (opcional)', type: 'checkbox' },
      { key: 'subscriptionGraceDays', label: 'Carência após o vencimento da assinatura (dias)', type: 'number', min: 0, max: 30 },
      { key: 'renewalReminderDays', label: 'Lembrar renovação quantos dias antes (0 = não)', type: 'number', min: 0, max: 15 },
      { key: 'loyaltyEnabled', label: 'Programa de fidelidade (pontos)', type: 'checkbox' },
      { key: 'loyaltyPointsPerReal', label: 'Pontos por R$ 1 gasto', type: 'number', step: '0.1', min: 0 },
      { key: 'loyaltyRewardPoints', label: 'Pontos para o prêmio', type: 'number', min: 1 },
      { key: 'loyaltyRewardDescription', label: 'Prêmio' },
      { key: 'language', label: 'Idioma do painel', type: 'select', options: [{ value: 'pt-BR', label: 'Português (Brasil)' }, { value: 'en', label: 'English' }] },
      { key: 'brandDisplayName', label: 'Nome exibido (marca própria)' },
      { key: 'brandColor', label: 'Cor da marca', type: 'color' },
      { key: 'logoUrl', label: 'Logo (endereço https://)' }
    ];
    root.innerHTML = `<div class="surface-card"><h3>Página pública, PIX, fidelidade e marca</h3><p class="ext-hint">Plano SaaS: <strong>${h(s.saasTier)}</strong> · até ${s.maxBarbers} profissional(is).</p>
      ${form(fields, { ...s, brandColor: s.brandColor || '#ea580c' })}<button type="button" class="btn btn-primary" data-x="save">Salvar recursos</button></div>
      <div class="surface-card"><h3>Segurança da sua conta</h3><div data-x="totp"></div></div>`;
    $('[data-x="save"]', root).addEventListener('click', guard(async () => {
      const r = await send('PUT', '/api/dashboard/extensions/settings', read(root, fields));
      toast('Recursos salvos.');
      window.CHBrand?.apply(r);
    }));
    renderTotp($('[data-x="totp"]', root));
  });

  async function renderTotp(box) {
    const st = await get('/api/auth/totp/status').catch(() => null);
    if (!st) { box.textContent = 'Indisponível.'; return; }
    box.innerHTML = st.enabled
      ? '<p>✅ Verificação em duas etapas <strong>ativa</strong>. Ao entrar, o painel pede o código do aplicativo autenticador.</p><button type="button" class="btn btn-danger" data-totp-off>Desativar</button>'
      : `<p>Proteja o acesso com um código do celular (Google Authenticator, Authy, 1Password…).${st.role === 'SuperAdmin' ? ' <strong>Recomendado para o SuperAdmin.</strong>' : ''}</p><button type="button" class="btn btn-primary" data-totp-on>Ativar verificação em duas etapas</button>`;
    box.querySelector('[data-totp-on]')?.addEventListener('click', guard(async () => {
      const setup = await send('POST', '/api/auth/totp/setup');
      const dlg = dialog('Ativar verificação em duas etapas', `<p>1. No aplicativo autenticador, adicione uma conta e digite a chave abaixo (ou use o link otpauth):</p>
        <p class="ext-mono ext-secret">${h(setup.secret.replace(/(.{4})/g, '$1 ').trim())}</p><p><small class="ext-mono">${h(setup.otpAuthUri)}</small></p>
        <div class="field"><label>2. Digite o código de 6 dígitos que aparece no app</label><input inputmode="numeric" maxlength="6" data-code></div>`, { footer: '<button type="button" class="btn btn-primary" data-confirm>Confirmar</button>' });
      $('[data-confirm]', dlg).addEventListener('click', guard(async () => {
        await send('POST', '/api/auth/totp/enable', { code: $('[data-code]', dlg).value });
        dlg.close(); toast('Verificação em duas etapas ativada.'); renderTotp(box);
      }));
    }));
    box.querySelector('[data-totp-off]')?.addEventListener('click', guard(async () => {
      const code = window.prompt('Para desativar, digite o código atual do aplicativo autenticador:');
      if (!code) return;
      await send('POST', '/api/auth/totp/disable', { code }); toast('Verificação em duas etapas desativada.'); renderTotp(box);
    }));
  }

  /* ================================================================== SUPERADMIN (68, 71–76) */
  document.addEventListener('ch:app-ready', (e) => {
    if (!e.detail.superAdmin || document.getElementById('tab-x-sa-signups')) return;
    addSubtab('section-superadmin', 'sa-signups', 'Cadastros', async (root) => {
      root.innerHTML = `<div class="surface-card"><div class="ext-toolbar"><h3 style="margin:0">Pedidos de cadastro</h3><select data-x="st"><option value="Pending">Pendentes</option><option value="all">Todos</option></select>
        <button type="button" class="btn btn-secondary" data-x="bill">Rodar cobrança automática agora</button></div>
        <p class="ext-hint">Formulário público em <a href="/cadastro" target="_blank" rel="noopener">${location.origin}/cadastro</a>. Ao aprovar, a loja é criada com 14 dias de teste e senha temporária (troca obrigatória no 1º acesso).</p><div data-x="list"></div></div>
        <div class="surface-card"><h3>Limites por plano SaaS</h3><div data-x="limits"></div></div>
        <div class="surface-card"><h3>Sua segurança</h3><div data-x="totp"></div></div>`;
      const load = guard(async () => {
        const list = await get(`/api/superadmin/signups?status=${$('[data-x="st"]', root).value}`);
        $('[data-x="list"]', root).innerHTML = list.length ? `<ul class="ext-list">${list.map(s => `<li class="ext-row"><div><strong>${h(s.storeName)}</strong> ${pill(s.status)}<br><small>${h(s.ownerName)} · ${h(s.ownerPhone)} · ${h(s.city || '')} · ${h(s.desiredPlan)} · ${new Date(s.createdAt).toLocaleString('pt-BR')}</small>${s.reviewNotes ? `<br><small>${h(s.reviewNotes)}</small>` : ''}</div>
          ${s.status === 'Pending' ? `<div class="ext-actions"><button type="button" class="btn btn-success btn-compact" data-ok="${s.id}">Aprovar</button><button type="button" class="btn btn-danger btn-compact" data-no="${s.id}">Recusar</button></div>` : ''}</li>`).join('')}</ul>` : empty('Nenhum pedido.');
        const limits = await get('/api/superadmin/plan-limits');
        $('[data-x="limits"]', root).innerHTML = table(['Plano', 'Profissionais', 'Campanhas/mês', 'Agendamento online', 'Estoque'], limits.map(l => [l.tier, l.maxBarbers, l.maxCampaignsPerMonth, l.publicBooking ? 'Sim' : 'Não', l.stock ? 'Sim' : 'Não']));
      });
      root.addEventListener('click', guard(async (ev) => {
        const ok = ev.target.closest('[data-ok]');
        if (ok) {
          const user = window.prompt('Usuário de login do dono da nova loja:');
          if (!user) return;
          const fee = window.prompt('Mensalidade (R$):', '149.90');
          const r = await send('POST', `/api/superadmin/signups/${ok.dataset.ok}/approve`, { adminUsername: user, monthlyFee: fee ? Number(fee.replace(',', '.')) : null });
          dialog('Loja criada', `<p>Loja #${r.storeId} criada para <strong>${h(r.signup.storeName)}</strong>.</p><p>Usuário: <strong>${h(r.adminUsername)}</strong><br>Senha temporária: <strong class="ext-mono">${h(r.temporaryPassword)}</strong></p><p class="ext-hint">Envie ao dono por um canal privado. Ela só aparece agora e precisa ser trocada no primeiro acesso.</p>`);
          return load();
        }
        const no = ev.target.closest('[data-no]');
        if (no) { await send('POST', `/api/superadmin/signups/${no.dataset.no}/reject`, { notes: window.prompt('Motivo (opcional):') || null }); return load(); }
        if (ev.target.closest('[data-x="bill"]')) { const r = await send('POST', '/api/superadmin/billing/run'); toast(`${r.chargesCreated} cobrança(s) gerada(s), ${r.storesUpdated} loja(s) atualizada(s).`); }
      }));
      $('[data-x="st"]', root).addEventListener('change', load);
      load();
      renderTotp($('[data-x="totp"]', root));
    });
  });
})();

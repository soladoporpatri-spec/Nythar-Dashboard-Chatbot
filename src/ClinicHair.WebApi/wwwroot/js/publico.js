/* Página pública da barbearia (/p/{slug}) e cadastro de novas lojas (/cadastro). Itens 66, 71, 94, 96 e 100. */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const money = (v) => new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(Number(v || 0));
  const esc = (s) => { const d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; };
  const localIso = (d) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);

  async function api(url, options = {}) {
    const res = await fetch(url, { ...options, headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } });
    if (res.status === 429) throw new Error('Muitas tentativas. Aguarde um minuto e tente de novo.');
    if (!res.ok) {
      let msg = 'Não foi possível concluir. Tente novamente.';
      try { const p = await res.json(); msg = p.detail || p.title || msg; } catch { /* sem corpo */ }
      throw new Error(msg);
    }
    return res.status === 204 || res.status === 202 ? null : res.json();
  }

  const path = location.pathname.replace(/\/+$/, '');
  if (path === '/cadastro') {
    document.title = 'Cadastre sua barbearia';
    $('store-name').textContent = 'Clinic Hair para barbearias';
    $('view-signup').hidden = false;
    $('signup-form').addEventListener('submit', async (e) => {
      e.preventDefault();
      const msg = $('signup-msg');
      const btn = e.submitter || e.target.querySelector('button');
      btn.disabled = true;
      msg.className = '';
      msg.textContent = 'Enviando…';
      try {
        await api('/api/public/signup', {
          method: 'POST',
          body: JSON.stringify({
            storeName: $('s-store').value, ownerName: $('s-owner').value, ownerPhone: $('s-phone').value,
            city: $('s-city').value, desiredPlan: $('s-plan').value, website: $('s-website').value
          })
        });
        msg.className = 'ok';
        msg.textContent = 'Pedido recebido! Entraremos em contato pelo WhatsApp informado.';
        e.target.reset();
      } catch (err) {
        msg.className = 'err';
        msg.textContent = err.message;
      } finally { btn.disabled = false; }
    });
    return;
  }

  const slug = decodeURIComponent(path.split('/')[2] || '');
  let store = null;
  let chosenTime = null;

  (async () => {
    try {
      store = await api(`/api/public/stores/${encodeURIComponent(slug)}`);
    } catch {
      $('store-name').textContent = 'Barbearia';
      $('view-missing').hidden = false;
      return;
    }
    document.title = `${store.name} — agendar horário`;
    $('store-name').textContent = store.name;
    $('store-info').innerHTML = [
      store.address ? esc(store.address) : '',
      store.mapLink && /^https:\/\//i.test(store.mapLink) ? `<a href="${esc(store.mapLink)}" target="_blank" rel="noopener">Ver no mapa</a>` : '',
      `Funcionamento ${esc(store.openingTime)}–${esc(store.closingTime)}`
    ].filter(Boolean).join(' · ');
    if (store.brandColor && /^#[0-9a-f]{6}$/i.test(store.brandColor)) document.documentElement.style.setProperty('--brand', store.brandColor);
    if (store.logoUrl && /^(https:\/\/|\/)/i.test(store.logoUrl)) { $('logo').src = store.logoUrl; $('logo').hidden = false; }
    if (store.language === 'en') document.documentElement.lang = 'en';
    $('view-store').hidden = false;

    if (store.plans.length) {
      $('plans-wrap').hidden = false;
      $('plans').innerHTML = store.plans.map(p => `<div class="plan"><strong>${esc(p.name)}</strong><span class="price">${money(p.price)}</span>
        <span class="muted small">${p.usesPerMonth ? `${p.usesPerMonth} uso(s)` : 'Uso ilimitado'} a cada ${p.validityDays} dias${p.maxMembers > 1 ? ` · até ${p.maxMembers} pessoas` : ''}</span>
        <span class="small">${esc(p.includedServices.join(', '))}</span>${p.description ? `<span class="muted small">${esc(p.description)}</span>` : ''}</div>`).join('');
    }

    if (!store.bookingEnabled) return;
    $('booking').hidden = false;
    $('b-service').innerHTML = store.services.map(s => `<option value="${s.id}">${esc(s.name)} — ${money(s.price)} (${s.durationMinutes} min)</option>`).join('');
    $('b-barber').innerHTML = '<option value="0">Qualquer um</option>' + store.barbers.map(b => `<option value="${b.id}">${esc(b.name)}</option>`).join('');
    const today = new Date();
    $('b-date').min = localIso(today);
    $('b-date').max = localIso(new Date(today.getTime() + store.bookingHorizonDays * 86400000));
    $('b-date').value = localIso(today);
    ['b-service', 'b-barber', 'b-date'].forEach(id => $(id).addEventListener('change', loadSlots));
    loadSlots();
  })();

  async function loadSlots() {
    chosenTime = null;
    const box = $('b-slots');
    box.innerHTML = '<p class="muted">Buscando horários…</p>';
    try {
      const qs = new URLSearchParams({ barberId: $('b-barber').value, date: $('b-date').value, serviceId: $('b-service').value });
      const slots = await api(`/api/public/stores/${encodeURIComponent(slug)}/slots?${qs}`);
      box.innerHTML = slots.length
        ? slots.map(t => `<button type="button" class="slot" role="radio" aria-checked="false" data-t="${esc(t)}">${esc(t)}</button>`).join('')
        : '<p class="muted">Sem horários livres neste dia. Tente outro dia ou profissional.</p>';
    } catch (err) {
      box.innerHTML = `<p class="err">${esc(err.message)}</p>`;
    }
  }

  $('b-slots').addEventListener('click', (e) => {
    const b = e.target.closest('.slot');
    if (!b) return;
    chosenTime = b.dataset.t;
    document.querySelectorAll('.slot').forEach(x => x.setAttribute('aria-checked', String(x === b)));
  });

  $('book-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const msg = $('book-msg');
    msg.className = '';
    if (!chosenTime) { msg.className = 'err'; msg.textContent = 'Escolha um horário.'; return; }
    const btn = e.submitter || e.target.querySelector('button[type="submit"]');
    btn.disabled = true;
    msg.textContent = 'Confirmando…';
    try {
      const r = await api(`/api/public/stores/${encodeURIComponent(slug)}/book`, {
        method: 'POST',
        body: JSON.stringify({
          name: $('b-name').value, phone: $('b-phone').value, serviceId: Number($('b-service').value),
          barberId: Number($('b-barber').value), date: $('b-date').value, time: chosenTime, website: $('b-website').value
        })
      });
      const [y, m, d] = r.date.split('-');
      msg.className = 'ok';
      msg.innerHTML = `Agendado! ${esc(r.serviceName)} com ${esc(r.barberName)} em ${d}/${m} às ${esc(r.time)} (${money(r.price)}).` +
        (r.pixCopyPaste ? `<br><br>Se quiser, pague agora pelo PIX copia e cola:<div class="pix">${esc(r.pixCopyPaste)}</div><button type="button" class="btn" id="copy-pix">Copiar código PIX</button>` : '');
      $('copy-pix')?.addEventListener('click', () => navigator.clipboard?.writeText(r.pixCopyPaste));
      loadSlots();
    } catch (err) {
      msg.className = 'err';
      msg.textContent = err.message;
      loadSlots();
    } finally { btn.disabled = false; }
  });
})();

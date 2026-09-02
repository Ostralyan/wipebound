// Everything below builds HTML from strings other people chose. A display name
// is picked by a player, and a boss or ability name arrives from a game server,
// so both are attacker-controlled as far as this page is concerned.
//
// Names legitimately contain apostrophes and accents, so the answer is to escape
// them here rather than to forbid them at the source: "<svg/onload=alert(1)>"
// fits inside the twenty-four character limit and used to run on the ladder.
//
// Canvas text needs none of this: fillText draws glyphs, not markup.
const esc = value => String(value ?? '').replace(/[&<>"']/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// Identifiers go into hrefs, which is a different context with different rules.
const url = value => encodeURIComponent(String(value ?? ''));

const TINT = { Warden:'#38bdf8', Ember:'#fb923c', Verdant:'#4ade80' };
const ARENA = 46;
let timer = null;

const fmt = n => Math.round(n).toLocaleString();
const secs = ms => `${Math.floor(ms/60000)}:${String(Math.floor(ms/1000)%60).padStart(2,'0')}`;
const get = async path => (await fetch(path)).json();

addEventListener('hashchange', route);

async function route() {
  if (timer) { cancelAnimationFrame(timer); timer = null; }
  const [view, arg] = location.hash.slice(1).split('/');

  if (view === 'run') return showRun(arg);
  if (view === 'player') return showPlayer(arg);
  if (view === 'ladder') return ladder(arg);
  return recent();
}

// -- lists -------------------------------------------------------------------

function list(rows) {
  document.getElementById('side').innerHTML = rows.join('');
}

async function recent() {
  const runs = await get('/v1/runs/recent');
  if (!runs.length) return list(['<p class="muted" style="padding:16px">No runs yet.</p>']);

  list(runs.map(r => `<a class="run" href="#run/${url(r.id)}">
      <b>${esc(r.boss)}</b>
      <span class="muted">${secs(r.duration_ms)} · ${esc(r.outcome)}${r.rankable ? ' · ranked' : ''}</span>
    </a>`));

  if (!location.hash.startsWith('#run/')) location.hash = `#run/${runs[0].id}`;
}

/// The ladder is what every content hash, digest and movement check upstream
/// exists to make trustworthy, so it gets its own view rather than being an
/// endpoint nothing calls.
async function ladder(boss) {
  const bosses = await get('/v1/bosses');
  if (!bosses.length) return list(['<p class="muted" style="padding:16px">No runs yet.</p>']);

  boss = boss || bosses[0].boss;
  list(bosses.map(b => `<a class="run ${b.boss === boss ? 'on' : ''}" href="#ladder/${url(b.boss)}">
      <b>${esc(b.boss)}</b><span class="muted">${b.attempts} attempts</span></a>`));

  const entries = await get(`/v1/leaderboards/${encodeURIComponent(boss)}`);
  document.getElementById('sub').textContent = `${boss} · fastest clears`;

  document.getElementById('detail').innerHTML = entries.length
    ? `<h2>Fastest clears</h2><table>
        <tr><th>#</th><th>Time</th><th>Group</th><th></th></tr>` +
      entries.map((e, i) => `<tr>
        <td class="n">${i + 1}</td>
        <td class="n">${secs(e.duration_ms)}</td>
        <td>${e.players.map(p => `${esc(p.display_name)}${p.identity === 'anonymous' ? '' : ' ✓'}`).join(', ')}</td>
        <td><a class="who" href="#run/${url(e.id)}">log</a></td>
      </tr>`).join('') + '</table>' +
      '<p class="muted">A tick marks an identity the server verified. Everything else is a name somebody chose.</p>'
    : '<p class="muted">Nothing ranked on this balance patch yet.</p>';
}

async function showPlayer(id) {
  const data = await get(`/v1/players/${id}`);
  document.getElementById('sub').textContent = data.display_name;

  document.getElementById('detail').innerHTML = `<h2>${esc(data.display_name)}</h2><table>
    <tr><th>Boss</th><th>Result</th><th>Damage</th><th>Healing</th><th>Avoidable</th><th>Deaths</th><th></th></tr>` +
    data.attempts.map(a => `<tr>
      <td>${esc(a.run.boss)}</td><td>${esc(a.run.outcome)}</td>
      <td class="n">${fmt(a.stats?.damage_done ?? 0)}</td>
      <td class="n">${fmt(a.stats?.healing_done ?? 0)}</td>
      <td class="n">${fmt(a.stats?.avoidable_damage ?? 0)}</td>
      <td class="n">${a.stats?.deaths ?? 0}</td>
      <td><a class="who" href="#run/${url(a.run.id)}">log</a></td>
    </tr>`).join('') + '</table>';
}

// -- one run -----------------------------------------------------------------

async function showRun(id) {
  const data = await get(`/v1/runs/${id}`);
  document.getElementById('sub').textContent =
    `${data.run.boss} · ${secs(data.run.duration_ms)} · ${data.run.outcome}`;

  const seconds = Math.max(data.run.duration_ms / 1000, 1);
  const stats = data.stats || [];
  const who = p => `<a class="who ${esc(p.class_name)}" href="#player/${url(p.player_id)}">${esc(p.display_name)}</a>`;

  const meter = (title, pick, extra) => {
    if (!stats.some(pick)) return '';
    const ranked = [...stats].sort((a, b) => pick(b) - pick(a));
    const top = Math.max(pick(ranked[0]), 1);
    return `<h2>${title}</h2><table><tr><th>Player</th><th>Total</th><th>Per second</th>${extra ? `<th>${extra.label}</th>` : ''}</tr>` +
      ranked.map(p => `<tr>
        <td class="bar"><i style="background:${TINT[p.class_name] || '#889'};width:${100 * pick(p) / top}%"></i>
          <span>${who(p)} <span class="muted">${esc(p.class_name)}</span></span></td>
        <td class="n">${fmt(pick(p))}</td>
        <td class="n">${fmt(pick(p) / seconds)}</td>
        ${extra ? `<td class="n">${extra.cell(p)}</td>` : ''}
      </tr>`).join('') + '</table>';
  };

  const abilities = (data.abilities || []).filter(a => a.damage || a.healing || a.casts);
  const named = new Map(stats.map(p => [p.combat_id, p.display_name]));

  document.getElementById('detail').innerHTML =
    meter('Damage done', p => p.damage_done) +
    meter('Healing done', p => p.healing_done, { label:'Overheal', cell: p => fmt(p.overhealing) }) +
    meter('Damage taken', p => p.damage_taken, {
      label:'Avoidable',
      cell: p => `${fmt(p.avoidable_damage)} (${Math.round(100 * p.avoidable_damage / Math.max(p.damage_taken, 1))}%)`,
    }) +
    meter('Resource spent', p => p.resource_spent) +
    (stats.length ? `<h2>Interrupts, dispels and deaths</h2><table>
      <tr><th>Player</th><th>Interrupts</th><th>Dispels</th><th>Deaths</th><th>Alive</th></tr>` +
      stats.map(p => `<tr><td>${who(p)}</td>
        <td class="n">${p.interrupts}</td><td class="n">${p.dispels}</td>
        <td class="n">${p.deaths}</td><td class="n">${secs(p.alive_ms)}</td></tr>`).join('') + '</table>' : '') +
    (abilities.length ? `<h2>By ability</h2><table>
      <tr><th>Player</th><th>Ability</th><th>Damage</th><th>Healing</th><th>Hits</th><th>Casts</th><th>Cost</th></tr>` +
      abilities.map(a => `<tr><td>${esc(named.get(a.combat_id) ?? a.combat_id)}</td><td>${esc(a.ability)}</td>
        <td class="n">${fmt(a.damage)}</td><td class="n">${fmt(a.healing)}</td>
        <td class="n">${a.hits}</td><td class="n">${a.casts}</td>
        <td class="n">${fmt(a.resource_spent)}</td></tr>`).join('') + '</table>' : '') +
    (data.has_log ? `<h2>Replay</h2>
      <div class="phases" id="phases"></div>
      <div class="controls"><button id="play">Play</button>
        <input type="range" id="scrub" min="0" max="${data.run.duration_ms}" value="0">
        <span class="muted" id="clock">0:00</span></div>
      <canvas id="stage" width="720" height="720"></canvas>`
      // Two different absences, and saying "expired" for both was misleading.
      // Statistics outlive the blob they came from, so numbers without a log
      // means the replay was pruned; nothing at all means none ever arrived.
      : stats.length
        ? '<p class="muted">The replay for this run has expired. Its numbers have not.</p>'
        : '<p class="muted">No combat log was uploaded for this run, so there is nothing to show.</p>');

  if (data.has_log) replay(id, data.run.duration_ms);
}

// -- replay ------------------------------------------------------------------

async function replay(id, duration) {
  // Served with Content-Encoding: gzip, so the browser has already inflated it.
  const log = await get(`/v1/runs/${id}/log`);
  const names = log.names || log.abilities || [];
  const stage = document.getElementById('stage'), ctx = stage.getContext('2d');
  const scrub = document.getElementById('scrub'), clock = document.getElementById('clock');
  const play = document.getElementById('play'), phaseBar = document.getElementById('phases');
  const actorOf = Object.fromEntries(log.actors.map(a => [a.id, a]));

  // Sized from the document rather than assumed, so a log written by an older
  // build still reads: format 1 had four fields per sample, format 2 has five.
  const stride = log.tracks.stride.length;
  const slot = field => log.tracks.stride.indexOf(field);
  const absent = log.tracks.absent;

  const R = stage.width / 2, K = R / (ARENA * 1.05);
  const sx = x => R + x * K, sz = z => R + z * K;

  const phases = log.events.filter(e => e[1] === 12).map(e => ({ at: e[0], name: names[e[4]] ?? '?' }));

  // Keyed by SOURCE as well as name. Burning and Hunted exist separately per
  // caster, so keying by name alone would let one caster's instance overwrite
  // another's and a single removal clear them all.
  function aurasAt(at) {
    const held = {};
    for (const [t, kind, source, target, name] of log.events) {
      if (t > at) break;
      const held_ = held[target] ??= new Map();
      if (kind === 5) held_.set(`${source}:${name}`, names[name]);
      else if (kind === 6) held_.delete(`${source}:${name}`);
    }
    return held;
  }

  function bar(x, y, w, value, colour) {
    ctx.fillStyle = '#1b2430'; ctx.fillRect(x, y, w, 3);
    ctx.fillStyle = colour; ctx.fillRect(x, y, w * value, 3);
  }

  function draw(at) {
    ctx.clearRect(0, 0, stage.width, stage.height);
    ctx.strokeStyle = '#1b2430'; ctx.beginPath(); ctx.arc(R, R, ARENA * K, 0, 7); ctx.stroke();

    for (const [group, kind] of [[log.hazards, 'hazard'], [log.telegraphs, 'telegraph']])
      for (const item of group) {
        if (at < item.from_ms || at > item.until_ms) continue;
        const a = item.area, wound = (at - item.from_ms) / Math.max(1, item.until_ms - item.from_ms);
        ctx.fillStyle = `#${item.colour}`;
        ctx.globalAlpha = kind === 'hazard' ? .22 : .14 + .3 * wound;
        ctx.beginPath();
        if (a.shape === 1) {
          ctx.moveTo(sx(a.cx), sz(a.cz));
          ctx.arc(sx(a.cx), sz(a.cz), a.radius * K,
                  -a.facing - Math.PI/2 - a.half_angle, -a.facing - Math.PI/2 + a.half_angle);
        } else {
          ctx.arc(sx(a.cx), sz(a.cz), a.radius * K, 0, 7);
        }
        ctx.fill(); ctx.globalAlpha = 1;
      }

    ctx.fillStyle = '#fb923c';
    for (const shot of log.projectiles) {
      if (at < shot.from_ms || at > shot.until_ms) continue;
      const d = (at - shot.from_ms) / 1000 * shot.speed_cms / 100;
      ctx.beginPath();
      ctx.arc(sx(shot.x_cm/100 + shot.dx*d), sz(shot.z_cm/100 + shot.dz*d),
              Math.max(2, shot.radius_cm/100*K), 0, 7);
      ctx.fill();
    }

    const held = aurasAt(at);
    const i = Math.min(Math.floor(at / log.tracks.interval_ms), log.tracks.samples - 1);

    for (const [id, lane] of Object.entries(log.tracks.lanes)) {
      const base = i * stride;
      if (base + stride > lane.length || lane[base] === absent) continue;

      const sample = lane.slice(base, base + stride);
      const x = sample[slot('x_cm')] / 100, z = sample[slot('z_cm')] / 100;
      const facing = sample[slot('facing_decideg')], health = sample[slot('health_permille')];
      const manaSlot = slot('mana_permille');
      const mana = manaSlot >= 0 ? sample[manaSlot] : absent;

      const actor = actorOf[id] || {};
      const px = sx(x), pz = sz(z);

      ctx.fillStyle = actor.kind === 'boss' ? '#f43f5e' : (TINT[actor.class] || '#8aa0b4');
      ctx.beginPath(); ctx.arc(px, pz, actor.kind === 'boss' ? 9 : 6, 0, 7); ctx.fill();

      const yaw = -facing/10 * Math.PI/180 - Math.PI/2;
      ctx.strokeStyle = ctx.fillStyle; ctx.beginPath();
      ctx.moveTo(px, pz); ctx.lineTo(px + Math.cos(yaw)*12, pz + Math.sin(yaw)*12); ctx.stroke();

      bar(px - 14, pz - 16, 28, health/1000, '#4ade80');
      if (mana !== absent) bar(px - 14, pz - 11, 28, mana/1000, '#60a5fa');

      ctx.fillStyle = '#e6edf3'; ctx.font = '11px sans-serif';
      ctx.fillText(actor.name ?? id, px + 11, pz + 1);

      const auras = [...(held[id]?.values() ?? [])];
      if (auras.length) {
        ctx.fillStyle = '#fbbf24'; ctx.font = '10px sans-serif';
        ctx.fillText(auras.join(' '), px + 11, pz + 12);
      }
    }
  }

  function showPhase(at) {
    const current = phases.filter(p => p.at <= at).pop();
    phaseBar.innerHTML = phases.length
      ? phases.map(p => `<span>${secs(p.at)} ${p === current ? `<b>${esc(p.name)}</b>` : esc(p.name)}</span>`).join('')
      : '';
  }

  let at = 0, running = false, last = 0;
  const step = now => {
    if (running) {
      at = Math.min(at + (now - last), duration);
      scrub.value = at; clock.textContent = secs(at);
      if (at >= duration) { running = false; play.textContent = 'Play'; }
    }
    last = now; draw(at); showPhase(at); timer = requestAnimationFrame(step);
  };

  scrub.oninput = () => { at = +scrub.value; clock.textContent = secs(at); };
  play.onclick = () => { running = !running; play.textContent = running ? 'Pause' : 'Play'; };
  timer = requestAnimationFrame(step);
}

route();

// Study Stash · Canvas: a read-only fetcher for your Study Stash library.
// The library decides what to read (assignments, submissions, modules, pages, files, or whatever a course's
// scout asks for); this extension only fetches those Canvas URLs with the session you're already
// signed into and hands the answers back. It never writes to Canvas, and it refuses any URL outside Canvas
// and its file CDN. It checks every second or two only while the library has work queued.
importScripts('config.js'); // STUDY_STASH = {app, key, canvas}, written by Study Stash

const MAX_BYTES = 40 * 1024 * 1024;

function allowed(url) {
  return url.startsWith(STUDY_STASH.canvas + '/') || /^https:\/\/[a-z0-9.-]+\.inscloudgate\.net\//.test(url);
}

async function app(path, body) {
  const r = await fetch(STUDY_STASH.app + path, {
    method: body ? 'POST' : 'GET',
    headers: {'X-Study-Stash-Key': STUDY_STASH.key, 'Content-Type': 'application/json'},
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.json();
}

function b64(buf) {
  const bytes = new Uint8Array(buf);
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

// Some files (often your own submissions) don't download through Canvas's /files/<id>/download redirect from a
// service worker. Canvas's API hands out a signed link to the same file, which does.
async function viaPublicUrl(url) {
  const m = url.match(/\/files\/(\d+)\/download/);
  if (!m) throw new Error('not a Canvas file');
  const t = await (await fetch(`${STUDY_STASH.canvas}/api/v1/files/${m[1]}/public_url`, {credentials: 'include'})).text();
  const signed = JSON.parse(t.replace(/^while\(1\);/, '')).public_url;
  if (!signed || !allowed(signed)) throw new Error('no public link');
  return fetch(signed);
}

async function run(job) {
  if (!allowed(job.url)) return {id: job.id, error: 'refused: not a Canvas URL'};
  try {
    const headers = job.kind === 'json' ? {Accept: 'application/json'} : {};
    let r;
    try {
      r = await fetch(job.url, {credentials: 'include', headers});
    } catch (e) {
      if (job.kind !== 'bytes') throw e;
      r = await viaPublicUrl(job.url);
    }
    const out = {id: job.id, status: r.status, link: r.headers.get('link') || '', type: r.headers.get('content-type') || '',
                 final: r.url};
    if (/\/login(\/|\?|$)/.test(new URL(r.url).pathname)) return {...out, status: 401};  // bounced to sign-in
    if (job.kind === 'bytes') {
      const buf = await r.arrayBuffer();
      if (buf.byteLength > MAX_BYTES) return {...out, error: 'too big'};
      return {...out, b64: b64(buf)};
    }
    const text = await r.text();
    if (job.kind === 'json' && r.ok && text.trimStart().startsWith('<')) return {...out, status: 401};
    return {...out, text: job.kind === 'json' ? text.replace(/^while\(1\);/, '') : text.slice(0, 2000000)};
  } catch (e) {
    return {id: job.id, error: String(e)};
  }
}

let running = false;
async function pump(force) {
  if (running) return;
  running = true;
  try {
    let idle = 0;
    for (let round = 0; round < 5000; round++) {
      let work;
      try { work = await app('/api/v2/canvas/work?v=' + chrome.runtime.getManifest().version + (force && round === 0 ? '&force=1' : '')); } catch (e) { return; }
      // Study Stash keeps this folder up to date; when it holds a newer version, reload from disk to pick it up
      if (work.ext && work.ext !== chrome.runtime.getManifest().version) {
        try {
          const onDisk = await (await fetch(chrome.runtime.getURL('manifest.json'), {cache: 'no-store'})).json();
          if (onDisk.version === work.ext) { chrome.runtime.reload(); return; }  // only when the new files are really there
        } catch (e) {}
      }
      if (work.jobs.length) {
        idle = 0;
        const results = await Promise.all(work.jobs.map(run));
        await app('/api/v2/canvas/results', {results});
        continue;
      }
      if (!work.hot || ++idle > 60) return;           // nothing queued and no agent exploring: sleep until the alarm
      await new Promise(r => setTimeout(r, 1500));   // an agent is exploring: check again shortly
    }
  } finally {
    running = false;
  }
}

function schedule() { chrome.alarms.create('sync', {periodInMinutes: 1}); }
chrome.runtime.onInstalled.addListener(() => { schedule(); pump(true); });
chrome.runtime.onStartup.addListener(() => { schedule(); pump(false); });
chrome.alarms.onAlarm.addListener(a => { if (a.name === 'sync') pump(false); });
// the toolbar popup asks for a sync
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg && msg.sync) { pump(true); reply({ok: true}); }
});

// The toolbar popup: when Canvas last synced, sync now, or open the library.
const s = document.getElementById('s');
function ago(iso) {
  if (!iso) return 'never';
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? 'just now' : m < 60 ? m + ' min ago' : Math.round(m / 60) + ' h ago';
}
fetch(STUDY_STASH.app + '/api/v2/canvas/status', {headers: {'X-Study-Stash-Key': STUDY_STASH.key}})
  .then(r => r.json())
  .then(j => { s.textContent = j.busy ? j.busy : (j.error ? j.error : 'Canvas synced ' + ago(j.synced) + '.'); })
  .catch(() => { s.textContent = "Your Study Stash library isn't answering."; });
document.getElementById('sync').onclick = () => {
  chrome.runtime.sendMessage({sync: true});
  s.textContent = 'Syncing… you can close this.';
};
document.getElementById('open').onclick = () => { chrome.tabs.create({url: STUDY_STASH.app + '/'}); window.close(); };

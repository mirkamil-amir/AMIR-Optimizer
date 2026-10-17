// Applies the saved theme before the first paint (no flash); lib/theme.tsx keeps it in sync afterwards.
// A same-origin file, not an inline script: the production CSP (nginx.conf) allows only script-src 'self'.
(function () {
  var mode = 'system', skin = 'navy'
  try {
    var v = JSON.parse(localStorage.getItem('amir-theme') || '{}')
    if (v.mode === 'light' || v.mode === 'dark' || v.mode === 'system') mode = v.mode
    if (v.skin === 'forest') v.skin = 'emerald'
    if (['graphite', 'navy', 'midnight', 'slate', 'ocean', 'teal', 'emerald', 'burgundy', 'amber', 'violet'].indexOf(v.skin) >= 0) skin = v.skin
  } catch { /* storage unavailable: defaults */ }
  var dark = mode === 'dark' || (mode === 'system' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches)
  var root = document.documentElement
  if (dark) root.classList.add('dark')
  root.dataset.skin = skin
  root.style.colorScheme = dark ? 'dark' : 'light'
})()

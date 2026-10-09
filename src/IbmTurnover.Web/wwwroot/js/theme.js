// Runs in <head> before first paint so the page never flashes the wrong theme. Dark by default.
(function () {
  try {
    var t = localStorage.getItem('tov-theme');
    if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t);
  } catch (e) { /* storage blocked: stay dark */ }
})();

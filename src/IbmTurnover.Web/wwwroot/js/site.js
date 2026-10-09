// IBM i Turnover — progressive enhancement only. Every page works without this file.
(function () {
  'use strict';
  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };

  // ---- theme ----------------------------------------------------------------
  $$('[data-theme-toggle]').forEach(function (b) {
    b.addEventListener('click', function () {
      var root = document.documentElement;
      var next = root.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
      root.setAttribute('data-theme', next);
      try { localStorage.setItem('tov-theme', next); } catch (e) { /* ignore */ }
    });
  });

  $$('[data-autosubmit]').forEach(function (s) { s.addEventListener('change', function () { s.form.submit(); }); });
  $$('[data-dismiss]').forEach(function (b) { b.addEventListener('click', function () { b.parentElement.remove(); }); });
  $$('form[data-confirm]').forEach(function (f) {
    f.addEventListener('submit', function (e) { if (!window.confirm(f.getAttribute('data-confirm'))) e.preventDefault(); });
  });
  $$('[data-swap]').forEach(function (b) {
    b.addEventListener('click', function () {
      var a = $('[data-env-from]'), z = $('[data-env-to]'), v = a.value;
      a.value = z.value; z.value = v;
    });
  });
  $$('[data-copy]').forEach(function (b) {
    b.addEventListener('click', function () {
      var el = $(b.getAttribute('data-copy'));
      if (!el || !navigator.clipboard) return;
      navigator.clipboard.writeText(el.textContent).then(function () {
        var t = b.innerHTML; b.textContent = 'Copied'; setTimeout(function () { b.innerHTML = t; }, 1400);
      });
    });
  });

  $$('[data-print]').forEach(function (b) { b.addEventListener('click', function () { window.print(); }); });

  // ---- report table: sort by column, filter with the same controls as the cards -------------
  $$('[data-report]').forEach(function (table) {
    var body = table.tBodies[0];
    $$('[data-sort]', table).forEach(function (btn) {
      btn.addEventListener('click', function () {
        var col = +btn.dataset.sort, dir = btn.dataset.dir === 'asc' ? 'desc' : 'asc';
        $$('[data-sort]', table).forEach(function (o) { delete o.dataset.dir; });
        btn.dataset.dir = dir;
        var rows = [].slice.call(body.rows);
        rows.sort(function (a, b) {
          var x = a.cells[col].textContent.trim(), y = b.cells[col].textContent.trim();
          return (dir === 'asc' ? 1 : -1) * x.localeCompare(y, undefined, { numeric: true, sensitivity: 'base' });
        });
        rows.forEach(function (r) { body.appendChild(r); });
      });
    });
  });
  var reportRows = $$('[data-row]');
  if (reportRows.length) {
    var rsev = 'all', rterm = '';
    var applyRows = function () {
      var shown = 0;
      reportRows.forEach(function (r) {
        var ok = (rsev === 'all' || r.dataset.sev === rsev) && (!rterm || r.dataset.search.indexOf(rterm) >= 0);
        r.hidden = !ok; if (ok) shown++;
      });
      var e = $('[data-empty-filter]'); if (e) e.hidden = shown > 0;
      $$('.toolbar [data-filter-sev]').forEach(function (b) { b.classList.toggle('active', b.dataset.filterSev === rsev); });
    };
    $$('[data-filter-sev]').forEach(function (b) { b.addEventListener('click', function () { rsev = b.dataset.filterSev; applyRows(); }); });
    var rs = $('[data-search]');
    if (rs) rs.addEventListener('input', function () { rterm = rs.value.trim().toLowerCase(); applyRows(); });
  }

  // ---- tabs -----------------------------------------------------------------
  $$('[data-tabs]').forEach(function (tabs) {
    var btns = $$('.tab-btn', tabs), panes = $$('.tab-pane', tabs);
    function show(name) {
      btns.forEach(function (b) { var on = b.getAttribute('data-tab') === name; b.classList.toggle('active', on); b.setAttribute('aria-selected', on); });
      panes.forEach(function (p) { var on = p.getAttribute('data-pane') === name; p.classList.toggle('active', on); p.hidden = !on; });
      try { sessionStorage.setItem('tov-tab:' + location.pathname, name); } catch (e) { /* ignore */ }
    }
    btns.forEach(function (b) { b.addEventListener('click', function () { show(b.getAttribute('data-tab')); }); });
    var saved = null;
    try { saved = sessionStorage.getItem('tov-tab:' + location.pathname); } catch (e) { /* ignore */ }
    if (saved && btns.some(function (b) { return b.getAttribute('data-tab') === saved; })) show(saved);
  });

  // ---- dashboard drift -------------------------------------------------------
  $$('[data-drift]').forEach(function (el) {
    // Relative to the app's base path (the site may be an IIS application under /something).
    var url = (document.body.getAttribute('data-base') || '/') + 'Home/Drift?source=' + encodeURIComponent(el.dataset.src) + '&target=' + encodeURIComponent(el.dataset.tgt);
    fetch(url, { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (d) {
        var count = $('[data-drift-count]', el), flags = $('[data-drift-flags]', el);
        flags.innerHTML = '';
        if (d.error) { count.textContent = '!'; flags.appendChild(pill('could not read', 'blocker')); el.title = d.error; return; }
        count.textContent = d.changes;
        if (d.blockers) flags.appendChild(pill(d.blockers + ' blocker' + (d.blockers > 1 ? 's' : ''), 'blocker'));
        if (d.warnings) flags.appendChild(pill(d.warnings + ' warning' + (d.warnings > 1 ? 's' : ''), 'warning'));
        if (!d.changes && !d.blockers) flags.appendChild(pill('in sync', 'fix'));
        el.title = Object.keys(d.byKind).map(function (k) { return k + ': ' + d.byKind[k]; }).join('\n');
      })
      .catch(function () { $('[data-drift-count]', el).textContent = '–'; $('[data-drift-flags]', el).innerHTML = ''; });
  });
  function pill(text, cls) { var s = document.createElement('span'); s.className = 'pill ' + cls; s.textContent = text; return s; }

  // ---- compare / package diff list -------------------------------------------
  var diffs = $$('[data-diff]');
  if (!diffs.length) return;
  var byId = {};
  diffs.forEach(function (d) { byId[d.dataset.id] = d; });

  function setOpen(d, open) {
    var body = $('.diff-body', d), btn = $('[data-expand]', d);
    body.hidden = !open; btn.setAttribute('aria-expanded', open);
    // Large comparisons: fetch the side-by-side definition the first time the card opens.
    var lazy = open && $('[data-sbs-url]', d);
    if (lazy && !lazy.dataset.loading) {
      lazy.dataset.loading = '1';
      fetch(lazy.dataset.sbsUrl, { credentials: 'same-origin', headers: { 'Accept': 'text/html' } })
        .then(function (r) { if (!r.ok) throw new Error(r.status); return r.text(); })
        .then(function (html) {
          // Same-origin partial rendered by Razor (encoded); parsed into nodes rather than assigned as markup.
          var doc = new DOMParser().parseFromString(html, 'text/html');
          lazy.replaceWith.apply(lazy, [].slice.call(doc.body.childNodes));
        })
        .catch(function () { lazy.textContent = 'Could not load the definitions. Refresh to try again.'; });
    }
  }
  diffs.forEach(function (d) {
    $('[data-expand]', d).addEventListener('click', function () { setOpen(d, $('.diff-body', d).hidden); });
    var list = $('[data-dep-list]', d);
    if (list) {
      deps(d).forEach(function (id) {
        var target = byId[id];
        if (!target) return;
        var a = document.createElement('a');
        a.href = '#' + target.id; a.textContent = target.dataset.name; a.dataset.depLink = id;
        a.addEventListener('click', function () { setOpen(target, true); target.classList.add('flash-target'); setTimeout(function () { target.classList.remove('flash-target'); }, 1600); });
        list.appendChild(a);
      });
    }
  });
  var expandAll = $('[data-expand-all]');
  if (expandAll) expandAll.addEventListener('click', function () {
    var open = expandAll.textContent.indexOf('Expand') === 0;
    diffs.forEach(function (d) { if (!d.hidden) setOpen(d, open); });
    expandAll.textContent = open ? 'Collapse all' : 'Expand all';
  });

  // filters
  var sev = 'all', term = '';
  function applyFilter() {
    var shown = 0;
    diffs.forEach(function (d) {
      var ok = (sev === 'all' || d.dataset.sev === sev) && (!term || d.dataset.search.indexOf(term) >= 0);
      d.hidden = !ok; if (ok) shown++;
    });
    $$('[data-group]').forEach(function (g) { g.hidden = !$$('[data-diff]', g).some(function (d) { return !d.hidden; }); });
    var empty = $('[data-empty-filter]'); if (empty) empty.hidden = shown > 0;
    $$('.toolbar [data-filter-sev]').forEach(function (b) { b.classList.toggle('active', b.dataset.filterSev === sev); });
  }
  $$('[data-filter-sev]').forEach(function (b) { b.addEventListener('click', function () { sev = b.dataset.filterSev; applyFilter(); }); });
  var search = $('[data-search]');
  if (search) search.addEventListener('input', function () { term = search.value.trim().toLowerCase(); applyFilter(); });

  // selection
  function box(d) { return $('[data-tick]', d); }
  function deps(d) { return (d.dataset.deps || '').split('|').filter(Boolean); }
  function refresh() {
    var count = 0, blockers = 0, missing = 0;
    diffs.forEach(function (d) {
      var b = box(d); if (!b) return;
      var unchecked = function (id) { return byId[id] && box(byId[id]) && !box(byId[id]).checked; };
      // 'any' = a level-check recompile: harmless (and dropped by the planner) when none of its causes ship.
      var lacking = b.checked && d.dataset.depsMode !== 'any' && deps(d).some(unchecked);
      $('[data-missing]', d).hidden = !lacking;
      $$('[data-dep-link]', d).forEach(function (a) { a.classList.toggle('missing', d.dataset.depsMode !== 'any' && !!unchecked(a.dataset.depLink)); });
      if (b.checked) { count++; if (d.dataset.openBlocker === '1') blockers++; if (lacking) missing++; }
    });
    var c = $('[data-sel-count]'); if (c) c.textContent = count;
    var bl = $('[data-sel-blockers]'); if (bl) { bl.hidden = !blockers; bl.textContent = blockers + ' with blockers'; }
    var mi = $('[data-sel-missing]'); if (mi) { mi.hidden = !missing; mi.textContent = missing + ' missing a dependency'; }
  }
  diffs.forEach(function (d) { var b = box(d); if (b) b.addEventListener('change', refresh); });
  $$('[data-select]').forEach(function (b) {
    b.addEventListener('click', function () {
      var mode = b.dataset.select;
      diffs.forEach(function (d) {
        var x = box(d); if (!x) return;
        x.checked = mode === 'all' ? true : mode === 'none' ? false : d.dataset.recommended === '1';
      });
      refresh();
    });
  });
  refresh();
})();

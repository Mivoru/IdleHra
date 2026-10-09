// Modul: THE LOADING SCREEN'S SCRIPT, as a file rather than inline in
// index.html, because the site sends a Content-Security-Policy with
// `script-src 'self'` (ops/oracle/caddy/Caddyfile). An inline script would
// need its hash in that header, and the day someone edited this script
// without updating the hash, the browser would refuse to run it - and the
// opaque loading picture would cover the game for ever, because the 25 s
// safety timeout below lives in this same script. As a same-origin file it
// is covered by 'self' whatever it says.
//
// A classic, parser-blocking script on purpose: window.__boot must exist
// before the module bundle runs (main.ts calls it first thing). Served
// no-cache, like index.html, so the two are always the same build.
// Modul: THE BAR MUST NOT LOOK STUCK. The real steps are few and uneven
// - the bundle download can take seconds on a phone and then four steps
// land within 200 ms - so between two reported steps the bar creeps
// toward the next step's percentage and never reaches it. It never goes
// backwards and never shows 100 until the app says it is done.
(function () {
  var root = document.getElementById('boot');
  if (!root) return;
  // Modul: THE SEASONAL EVENT'S LOOK BEFORE THE BUNDLE. eventTheme.ts writes
  // the key the wire last reported; reading it here puts the same data-event
  // on <html> and the event's loading art up from the first frame. Only AVIF
  // sources are swapped (the event art has no JPEG copy), and only a known key
  // - storage is not trusted to name a file. A first visit has no key and sees
  // the ordinary art, which is also what PageSpeed measures.
  try {
    var eventKey = localStorage.getItem('folkidle.eventTheme');
    if (eventKey === 'samhain') {
      document.documentElement.setAttribute('data-event', eventKey);
      var sources = root.querySelectorAll('source[type="image/avif"]');
      for (var i = 0; i < sources.length; i++) {
        sources[i].srcset = sources[i].srcset.replace(/\/loading\/(portrait|landscape)-/g, '/loading/' + eventKey + '-$1-');
      }
    }
  } catch (e) {
    // Blocked storage: the ordinary art.
  }
  var fill = root.querySelector('.boot-fill');
  var rail = root.querySelector('.boot-rail');
  var bar = root.querySelector('.boot-bar');
  var text = root.querySelector('.boot-text');
  var pct = root.querySelector('.boot-pct');
  var shown = 0;
  var ceiling = 30;
  var finished = false;
  function paint(value) {
    shown = Math.max(shown, Math.min(100, value));
    var whole = Math.round(shown);
    var slide = 'translateX(' + (shown - 100) + '%)';
    fill.style.transform = slide;
    rail.style.transform = slide;
    pct.textContent = whole + '%';
    bar.setAttribute('aria-valuenow', String(whole));
  }
  var creep = setInterval(function () {
    if (shown < ceiling - 1) paint(shown + (ceiling - shown) * 0.06);
  }, 250);
  function done() {
    if (finished) return;
    finished = true;
    clearInterval(creep);
    paint(100);
    setTimeout(function () {
      root.classList.add('boot-out');
      setTimeout(function () { root.remove(); }, 500);
    }, 250);
  }
  window.__boot = {
    step: function (percent, message, next) {
      if (finished) return;
      ceiling = Math.max(ceiling, next);
      if (message) text.textContent = message;
      paint(percent);
    },
    done: done,
  };
  // A boot that never reports (a bundle that throws before mounting)
  // must not leave the player behind an opaque picture for ever.
  setTimeout(done, 25000);
  paint(5);
})();

/* ==========================================================================
   The Game Library — interaction layer
   Everything here is progressive enhancement: the page reads and navigates
   fine with this file blocked. Only transform/opacity are animated.
   ========================================================================== */
(function () {
  'use strict';

  var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

  /* ---------------------------------------------------- magnetize CTA -- */
  /* Scatter N particles around each button; they spring to the centre while
     the control is hovered, focused or touched. Positions are generated once
     and stored on the element so release always returns to the same rest
     pose (the original component's behaviour). */

  var PARTICLE_COUNT = 14;
  var SPREAD_X = 100; // px
  var SPREAD_Y = 58; // px

  function buildParticles(btn) {
    // No particle field on reduced motion, and none on "coming soon" cards —
    // a dead control should not advertise itself as interactive.
    if (reduceMotion.matches || btn.getAttribute('aria-disabled') === 'true') return [];

    var particles = [];
    var frag = document.createDocumentFragment();

    for (var i = 0; i < PARTICLE_COUNT; i++) {
      var dot = document.createElement('span');
      dot.className = 'magnetize__particle';
      dot.setAttribute('aria-hidden', 'true');

      var x = Math.random() * SPREAD_X * 2 - SPREAD_X;
      var y = Math.random() * SPREAD_Y * 2 - SPREAD_Y;

      dot.style.transform = 'translate3d(' + x + 'px,' + y + 'px,0)';
      dot._rest = 'translate3d(' + x + 'px,' + y + 'px,0)';

      frag.appendChild(dot);
      particles.push(dot);
    }

    btn.insertBefore(frag, btn.firstChild);
    return particles;
  }

  function initMagnetize(btn) {
    var particles = buildParticles(btn);

    function attract() {
      btn.classList.add('is-attracting');
    }

    function release() {
      btn.classList.remove('is-attracting');
      // The class drives the converge transform; restoring rest poses here
      // lets each particle spring back to its own scattered spot.
      for (var i = 0; i < particles.length; i++) {
        particles[i].style.transform = particles[i]._rest;
      }
    }

    btn.addEventListener('mouseenter', attract);
    btn.addEventListener('mouseleave', release);
    btn.addEventListener('focus', attract);
    btn.addEventListener('blur', release);
    btn.addEventListener('touchstart', attract, { passive: true });
    btn.addEventListener('touchend', release);
    btn.addEventListener('touchcancel', release);

    // Disabled cards should not pretend to be actionable.
    if (btn.getAttribute('aria-disabled') === 'true') {
      btn.addEventListener('click', function (e) {
        e.preventDefault();
      });
    }
  }

  var buttons = document.querySelectorAll('.magnetize');
  for (var b = 0; b < buttons.length; b++) initMagnetize(buttons[b]);

  /* ------------------------------------------------- deck + dot nav -- */

  var stack = document.querySelector('.stack');
  var scenes = Array.prototype.slice.call(document.querySelectorAll('.scene'));
  var dots = Array.prototype.slice.call(document.querySelectorAll('.dotnav button'));
  var hero = document.querySelector('.hero');

  if (!stack || !scenes.length) return;

  var stackTop = 0;
  var sceneH = 0;
  var ticking = false;
  var lastActive = -1;

  function measure() {
    stackTop = stack.getBoundingClientRect().top + window.pageYOffset;
    sceneH = scenes[0].offsetHeight || window.innerHeight;
  }

  function clamp01(v) {
    return v < 0 ? 0 : v > 1 ? 1 : v;
  }

  function update() {
    ticking = false;
    var y = window.pageYOffset;

    /* Deck: p is how far the NEXT scene has travelled over this card.
       The outgoing card lifts and shrinks slightly so a sliver stays
       visible above the incoming one. */
    if (!reduceMotion.matches) {
      for (var i = 0; i < scenes.length; i++) {
        var card = scenes[i].querySelector('.card');
        if (!card) continue;

        var p = clamp01((y - (stackTop + i * sceneH)) / sceneH);
        var scale = 1 - 0.055 * p;
        var ty = -8 * p; // percent
        var fade = 1 - 0.35 * clamp01((p - 0.45) / 0.55);

        card.style.transform = 'translate3d(0,' + ty + '%,0) scale(' + scale + ')';
        card.style.opacity = fade;
      }
    }

    /* Dot nav: hero is index 0, then one dot per card. */
    var active;
    if (y < stackTop - sceneH * 0.5) {
      active = 0;
    } else {
      active = Math.floor((y - stackTop) / sceneH) + 1;
      if (active < 1) active = 1;
      if (active > scenes.length) active = scenes.length;
    }

    if (active !== lastActive) {
      lastActive = active;
      for (var d = 0; d < dots.length; d++) {
        dots[d].setAttribute('aria-current', d === active ? 'true' : 'false');
      }
    }
  }

  function onScroll() {
    if (!ticking) {
      ticking = true;
      window.requestAnimationFrame(update);
    }
  }

  /* Dot clicks jump to the matching scene. */
  for (var k = 0; k < dots.length; k++) {
    (function (index) {
      dots[index].addEventListener('click', function () {
        var target = index === 0 ? 0 : stackTop + (index - 1) * sceneH;
        window.scrollTo({
          top: target,
          behavior: reduceMotion.matches ? 'auto' : 'smooth'
        });
      });
    })(k);
  }

  var resizeTimer;
  function onResize() {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(function () {
      measure();
      lastActive = -1;
      update();
    }, 120);
  }

  measure();
  update();

  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onResize);
  window.addEventListener('orientationchange', onResize);
  window.addEventListener('load', function () {
    measure();
    update();
  });

  // Respect a live change of the motion preference.
  if (typeof reduceMotion.addEventListener === 'function') {
    reduceMotion.addEventListener('change', function () {
      if (reduceMotion.matches) {
        for (var i = 0; i < scenes.length; i++) {
          var card = scenes[i].querySelector('.card');
          if (card) {
            card.style.transform = '';
            card.style.opacity = '';
          }
        }
      }
      update();
    });
  }

  if (hero) hero.setAttribute('data-ready', 'true');
})();

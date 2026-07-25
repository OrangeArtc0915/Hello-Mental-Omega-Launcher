/* ==========================================================================
   HMOL(wine) 启动器 官方网站 / Official Website
   --------------------------------------------------------------------------
   File:   js/main.js
   Build:  Vanilla JS, no dependencies
   Notes:  Progressive enhancement, no JS-framework runtime required.
   ========================================================================== */
(function () {
  'use strict';

  // -------------------------------------------------------------------------
  // Theme switcher (dark <-> light), persists via localStorage
  // -------------------------------------------------------------------------
  var STORAGE_KEY = 'hmol-theme';
  var root = document.documentElement;
  var themeBtn = document.getElementById('theme-toggle');

  function applyTheme(theme) {
    root.setAttribute('data-theme', theme);
    if (themeBtn) {
      themeBtn.setAttribute(
        'aria-label',
        theme === 'dark' ? '切换到浅色模式' : '切换到深色模式'
      );
    }
  }

  function getInitialTheme() {
    try {
      var saved = localStorage.getItem(STORAGE_KEY);
      if (saved === 'light' || saved === 'dark') return saved;
    } catch (e) { /* localStorage blocked */ }
    if (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches) {
      return 'light';
    }
    return 'dark';
  }

  applyTheme(getInitialTheme());

  if (themeBtn) {
    themeBtn.addEventListener('click', function () {
      var current = root.getAttribute('data-theme') || 'dark';
      var next = current === 'dark' ? 'light' : 'dark';
      applyTheme(next);
      try { localStorage.setItem(STORAGE_KEY, next); } catch (e) { /* noop */ }
    });
  }

  // -------------------------------------------------------------------------
  // Mobile navigation
  // -------------------------------------------------------------------------
  var navToggle = document.getElementById('nav-toggle');
  var mobileNav = document.getElementById('mobile-nav');

  function closeMobileNav() {
    if (!navToggle || !mobileNav) return;
    navToggle.setAttribute('aria-expanded', 'false');
    mobileNav.hidden = true;
  }

  if (navToggle && mobileNav) {
    navToggle.addEventListener('click', function () {
      var expanded = navToggle.getAttribute('aria-expanded') === 'true';
      navToggle.setAttribute('aria-expanded', expanded ? 'false' : 'true');
      mobileNav.hidden = expanded;
    });

    // Close after click on a link
    mobileNav.querySelectorAll('a').forEach(function (a) {
      a.addEventListener('click', closeMobileNav);
    });

    // Close on escape
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && navToggle.getAttribute('aria-expanded') === 'true') {
        closeMobileNav();
        navToggle.focus();
      }
    });

    // Close when resizing to desktop
    var resizeTimer;
    window.addEventListener('resize', function () {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () {
        if (window.innerWidth > 768) closeMobileNav();
      }, 120);
    });
  }

  // -------------------------------------------------------------------------
  // Header shadow on scroll
  // -------------------------------------------------------------------------
  var header = document.getElementById('site-header');
  if (header) {
    var updateHeader = function () {
      if (window.scrollY > 4) {
        header.style.boxShadow = '0 1px 0 var(--border)';
      } else {
        header.style.boxShadow = 'none';
      }
    };
    updateHeader();
    window.addEventListener('scroll', updateHeader, { passive: true });
  }

  // -------------------------------------------------------------------------
  // FAQ accordion: only one open at a time
  // -------------------------------------------------------------------------
  var faqItems = document.querySelectorAll('.faq-item');
  faqItems.forEach(function (item) {
    item.addEventListener('toggle', function () {
      if (item.open) {
        faqItems.forEach(function (other) {
          if (other !== item && other.open) other.open = false;
        });
      }
    });
  });

  // -------------------------------------------------------------------------
  // Smooth in-page anchor scrolling with header offset
  // -------------------------------------------------------------------------
  document.querySelectorAll('a[href^="#"]').forEach(function (link) {
    link.addEventListener('click', function (e) {
      var href = link.getAttribute('href');
      if (!href || href === '#' || href.length < 2) return;
      var target = document.querySelector(href);
      if (!target) return;
      e.preventDefault();
      var headerH = header ? header.offsetHeight : 0;
      var y = target.getBoundingClientRect().top + window.scrollY - headerH - 8;
      window.scrollTo({ top: y, behavior: 'smooth' });
    });
  });

  // -------------------------------------------------------------------------
  // Subtle reveal on scroll using IntersectionObserver
  // -------------------------------------------------------------------------
  if ('IntersectionObserver' in window) {
    var revealObserver = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-revealed');
          revealObserver.unobserve(entry.target);
        }
      });
    }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });

    document.querySelectorAll(
      '.bento-cell, .theme-card, .download-card, .step, .media-card, .about-card, .section-head'
    ).forEach(function (el) {
      el.classList.add('reveal');
      revealObserver.observe(el);
    });
  }
})();

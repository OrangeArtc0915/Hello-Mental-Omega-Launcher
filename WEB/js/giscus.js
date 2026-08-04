/* ==========================================================================
   HMOL Forum - Giscus Integration
   ========================================================================== */
(function () {
  'use strict';

  var REPO = 'OrangeArtc0915/Hello-Mental-Omega-Launcher';
  var REPO_ID = 'R_kgDOQbTmcQ';

  window.HMOLGiscus = {
    load: function (discussionNumber, containerEl) {
      if (!containerEl) return;
      if (!discussionNumber) {
        containerEl.innerHTML = '<p style="text-align:center;color:var(--text-3);padding:32px;">no discussion.</p>';
        return;
      }
      containerEl.innerHTML = '';

      var script = document.createElement('script');
      script.src = 'https://giscus.app/client.js';
      script.setAttribute('data-repo', REPO);
      script.setAttribute('data-repo-id', REPO_ID);
      script.setAttribute('data-mapping', 'number');
      script.setAttribute('data-term', String(discussionNumber));
      script.setAttribute('data-reactions-enabled', '1');
      script.setAttribute('data-emit-metadata', '0');
      script.setAttribute('data-input-position', 'bottom');
      script.setAttribute('data-theme', 'preferred_color_scheme');
      script.setAttribute('data-lang', 'zh-CN');
      script.setAttribute('crossorigin', 'anonymous');
      script.async = true;

      containerEl.appendChild(script);
    }
  };
})();

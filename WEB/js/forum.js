/* ==========================================================================
   HMOL Forum - Core logic (static JSON data)
   ========================================================================== */
(function () {
  'use strict';

  var DATA_URL = 'data/discussions.json';

  var tempDiv = document.createElement('div');
  var dataCache = null;

  function stripHtml(html) {
    tempDiv.innerHTML = html || '';
    var text = tempDiv.textContent || tempDiv.innerText || '';
    return text.replace(/\s+/g, ' ').trim().substring(0, 200);
  }

  function timeAgo(dateStr) {
    var now = Date.now();
    var d = new Date(dateStr).getTime();
    var diff = Math.floor((now - d) / 1000);
    if (diff < 60) return 'just now';
    if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
    if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
    if (diff < 2592000) return Math.floor(diff / 86400) + 'd ago';
    return new Date(dateStr).toLocaleDateString('zh-CN');
  }

  function loadData() {
    if (dataCache) return Promise.resolve(dataCache);
    return fetch(DATA_URL)
      .then(function (r) {
        if (!r.ok) throw new Error('Data not available (status ' + r.status + ')');
        return r.json();
      })
      .then(function (data) {
        dataCache = data;
        return data;
      });
  }

  function postsByCategory(data, boardName) {
    return data.discussions.filter(function (d) {
      return d.category === boardName;
    });
  }

  function postByNumber(data, number) {
    return data.discussions.find(function (d) {
      return d.number === number;
    });
  }

  window.HMOLForum = {
    loadPosts: function (boardName, containerEl) {
      if (!containerEl) return;
      containerEl.innerHTML = '<div class="post-loading"><div class="spinner"></div><p>loading...</p></div>';

      loadData().then(function (data) {
        var posts = postsByCategory(data, boardName);

        if (!posts || posts.length === 0) {
          containerEl.innerHTML = '<div class="post-empty">No posts in this board</div>';
          return;
        }

        var html = '<div class="post-list">';
        posts.forEach(function (post) {
          var title = post.title || '(no title)';
          var excerpt = post.body_text || '';
          var author = post.author ? post.author.login : 'unknown';
          var avatar = post.author ? post.author.avatar_url : '';
          var category = post.category || '';
          var comments = post.comments_count || 0;
          var time = timeAgo(post.created_at);

          html += '<article class="post-item" data-number="' + post.number + '" onclick="location.href=\'post/#discussion-' + post.number + '\'">';
          html += '<div class="post-item-avatar">' + (avatar ? '<img src="' + avatar + '" alt="" loading="lazy" />' : '') + '</div>';
          html += '<div class="post-item-body">';
          html += '<div class="post-item-title">' + title + '</div>';
          if (excerpt) html += '<div class="post-item-excerpt">' + excerpt + '</div>';
          html += '</div>';
          html += '<div class="post-item-meta">';
          html += '<span class="post-item-tag">' + category + '</span>';
          html += '<span>' + author + '</span>';
          html += '<span>' + comments + ' replies</span>';
          html += '<span>' + time + '</span>';
          html += '</div>';
          html += '<span class="post-item-arrow" aria-hidden="true">';
          html += '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14M12 5l7 7-7 7" /></svg>';
          html += '</span>';
          html += '</article>';
        });
        html += '</div>';

        html += '<div style="text-align:center;margin-top:12px;font-size:11px;color:var(--text-3);">';
        html += 'Last updated: ' + new Date(data.updated_at).toLocaleString('zh-CN');
        html += '</div>';

        containerEl.innerHTML = html;
      }).catch(function (err) {
        containerEl.innerHTML = '<div class="post-error">Load failed: ' + err.message + '.<br><small>Data may not be generated yet. Push or run the "Fetch Discussions" workflow.</small></div>';
      });
    },

    loadPostDetail: function (discussionNumber, containerEl) {
      if (!containerEl || !discussionNumber) return;
      containerEl.innerHTML = '<div class="post-loading"><div class="spinner"></div><p>loading...</p></div>';

      loadData().then(function (data) {
        var d = postByNumber(data, discussionNumber);

        if (!d) {
          containerEl.innerHTML = '<div class="post-empty">Post not found</div>';
          return;
        }

        var author = d.author ? d.author.login : 'unknown';
        var avatar = d.author ? d.author.avatar_url : '';
        var category = d.category || '';
        var time = timeAgo(d.created_at);
        var comments = d.comments_count || 0;

        var html = '';
        html += '<article class="post-detail">';
        html += '<a class="post-detail-back" href="../">← Back to forum</a>';
        html += '<header class="post-detail-header">';
        html += '<h1 class="post-detail-title">' + (d.title || '(no title)') + '</h1>';
        html += '<div class="post-detail-meta">';
        html += '<span class="post-detail-author">' + (avatar ? '<img src="' + avatar + '" alt="" />' : '') + author + '</span>';
        html += '<span>' + time + '</span>';
        html += '<span>' + comments + ' replies</span>';
        html += '<span style="padding:3px 8px;background:var(--surface);border-radius:var(--r-pill);font-size:11px;color:var(--brand-1);">' + category + '</span>';
        html += '</div>';
        html += '</header>';
        html += '<div class="post-detail-body">' + (d.body || '<p>No content</p>') + '</div>';
        html += '</article>';
        html += '<div class="giscus-wrapper" id="giscus-container"></div>';

        containerEl.innerHTML = html;

        if (window.HMOLGiscus) {
          window.HMOLGiscus.load(d.number, document.getElementById('giscus-container'));
        }
      }).catch(function (err) {
        containerEl.innerHTML = '<div class="post-error">Load failed: ' + err.message + '</div>';
      });
    }
  };
})();

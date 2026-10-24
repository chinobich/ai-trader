/*
 * FolderJump 検索エンジン (Windows 版 src/*.cs の JavaScript 移植)
 * - TextNorm / Query / PathInput / FileIndex / UsageStore / QueryEngine
 * - サンプルの「ファイルサーバー」生成と、ブラウザで選んだフォルダからのインデックス作成
 */
(function (root) {
  'use strict';

  var DAY = 86400000;
  var SCOPE_BIT = 1 << 30;
  var EXCL_BIT = 1 << 29;
  var FLAG_DIR = 1;
  var FLAG_ROOT = 2;

  // ------------------------------------------------------------------
  // 文字列の正規化 (全角/半角・大文字/小文字・カタカナ/ひらがな を同一視)
  // ------------------------------------------------------------------
  function normKey(s) {
    if (!s) return '';
    var ascii = true;
    for (var i = 0; i < s.length; i++) {
      if (s.charCodeAt(i) >= 0x80) { ascii = false; break; }
    }
    if (ascii) return s.toLowerCase();
    var n = s;
    try { n = s.normalize('NFKC'); } catch (e) { /* 不正な文字列はそのまま */ }
    return n.toLowerCase()
      .replace(/[ァ-ヶヽヾ]/g, function (c) { return String.fromCharCode(c.charCodeAt(0) - 0x60); })
      .replace(/〜/g, '~');
  }

  // ------------------------------------------------------------------
  // 表記ゆれ辞書 (Windows 版 Synonyms.cs と同じ)
  // ------------------------------------------------------------------
  var DEFAULT_SYNONYMS = [
    '見積, 見積り, 見積もり, お見積, 御見積',
    '打合せ, 打ち合わせ, 打合わせ, 打ち合せ, ミーティング, MTG',
    '問合せ, 問い合わせ, 問合わせ, 問い合せ',
    '申込, 申し込み, 申込み',
    '引継ぎ, 引き継ぎ, 引継',
    '取扱, 取り扱い, 取扱い',
    '振込, 振り込み, 振込み',
    '受付, 受け付け, 受付け',
    '売上, 売り上げ, 売上げ',
    '株式会社, (株)',
    '有限会社, (有)',
    '議事録, 会議録, 議事メモ',
    '請求, インボイス, invoice',
    'マニュアル, 手順書, 手引き',
    'ヶ月, ケ月, か月, カ月, ヵ月, 箇月'
  ];

  function Synonyms(lines, era) {
    this.groups = [];
    this.era = era !== false;
    var self = this;
    (lines || []).forEach(function (line) {
      var words = [];
      line.split(/[,、，\s　]+/).forEach(function (raw) {
        var w = normKey(raw.trim());
        if (w && words.indexOf(w) < 0) words.push(w);
      });
      if (words.length < 2) return;
      words.sort(function (a, b) { return b.length - a.length; });
      self.groups.push(words);
    });
  }

  Synonyms.prototype.expand = function (term) {
    var set = [term];
    var add = function (s) { if (s && set.indexOf(s) < 0) set.push(s); };
    this.groups.forEach(function (g) {
      for (var i = 0; i < g.length; i++) {
        var w = g[i];
        if (term.indexOf(w) < 0) continue;
        g.forEach(function (o) { if (o !== w) add(term.split(w).join(o)); });
        break;
      }
    });
    if (this.era) eraAlternatives(term).forEach(add);
    var n = set.length;
    for (var k = 0; k < n; k++) add(dropLongVowels(set[k]));
    return prune(set);
  };

  function prune(set) {
    set.sort(function (a, b) { return a.length - b.length || (a < b ? -1 : a > b ? 1 : 0); });
    var kept = [];
    set.forEach(function (s) {
      if (!kept.some(function (k) { return s.indexOf(k) >= 0; })) kept.push(s);
    });
    return kept;
  }

  function isKana(c) {
    return (c >= 'ぁ' && c <= 'ゟ') || (c >= '゠' && c <= 'ヿ');
  }

  function dropLongVowels(s) {
    if (s.indexOf('ー') < 0) return '';
    var out = '';
    for (var i = 0; i < s.length; i++) {
      var c = s[i];
      if (c === 'ー' && i > 0 && isKana(s[i - 1]) && (i + 1 === s.length || !isKana(s[i + 1]))) continue;
      out += c;
    }
    return out === s ? '' : out;
  }

  function eraNames(y) {
    var names = [];
    if (y >= 2019 && y <= 2099) {
      var n = y - 2018;
      if (n === 1) names.push('令和元');
      names.push('令和' + n, 'r' + n);
      if (n < 10) names.push('r0' + n);
    }
    if (y >= 1989 && y <= 2019) {
      var h = y - 1988;
      if (h === 1) names.push('平成元');
      names.push('平成' + h, 'h' + h);
      if (h < 10) names.push('h0' + h);
    }
    return names;
  }

  /** 2024 → 令和6 / r6 / r06、令和6 → 2024 … */
  function eraAlternatives(term) {
    var m = /^(\d{4})(\D.*)?$/.exec(term);
    if (m) return eraNames(+m[1]).map(function (e) { return e + (m[2] || ''); });
    m = /^(令和|r|平成|h)(元|\d{1,2})(\D.*)?$/.exec(term);
    if (!m) return [];
    var n = m[2] === '元' ? 1 : +m[2];
    var reiwa = m[1] === '令和' || m[1] === 'r';
    if (n < 1 || (!reiwa && n > 31)) return [];
    var y = (reiwa ? 2018 : 1988) + n, rest = m[3] || '';
    return [y + rest].concat(eraNames(y).map(function (e) { return e + rest; }));
  }

  // ------------------------------------------------------------------
  // 検索語 (Windows 版 Query.cs と同じ書き方)
  //   見積 2024 / -旧 / .xlsx / .* / 営業/ / @今週 @7日 @2024-05 @2024年度 @r6 @2024以降
  // ------------------------------------------------------------------
  var KIND_ALL = 0, KIND_FOLDERS = 1, KIND_FILES = 2;

  function Query() {
    this.terms = []; this.alts = []; this.excludes = []; this.exts = [];
    this.kind = KIND_ALL; this.minTime = 0; this.maxTime = 0; this.preferFolders = false; this.dateLabel = null;
  }
  Query.prototype.hasFilters = function () { return this.exts.length > 0 || this.kind !== KIND_ALL || this.minTime > 0 || this.maxTime > 0; };
  Query.prototype.isEmpty = function () { return this.terms.length === 0 && !this.hasFilters(); };
  Query.prototype.describe = function () {
    var parts = [];
    if (this.kind === KIND_FOLDERS) parts.push('フォルダのみ');
    else if (this.kind === KIND_FILES && this.exts.length === 0) parts.push('ファイルのみ');
    if (this.exts.length > 0) parts.push('.' + this.exts.join(' .'));
    if (this.dateLabel) parts.push(this.dateLabel);
    return parts.join('  ·  ');
  };

  function parseQuery(text, synonyms, now) {
    var q = new Query();
    if (!text) return q;
    now = now || new Date();
    var terms = [], excl = [], exts = [];
    var toks = normKey(text).split(/[ \t　]+/);
    for (var i = 0; i < toks.length; i++) {
      var tok = toks[i];
      if (!tok || tok === '-') continue;
      if (tok === '/' || tok === '\\') { q.kind = KIND_FOLDERS; continue; }
      if (tok.length > 1 && tok[0] === '@' && tryDate(q, tok.slice(1), now)) continue;
      if (/^\.([a-z0-9]{1,10}|\*)$/.test(tok)) {
        var e = tok.slice(1);
        if (e === '*') q.kind = KIND_FILES;
        else if (exts.indexOf(e) < 0) exts.push(e);
        continue;
      }
      var body = tok, exclude = body.length > 1 && body[0] === '-';
      if (exclude) body = body.slice(1);
      var last = body[body.length - 1];
      if (!exclude && body.length > 1 && (last === '/' || last === '\\')) q.kind = KIND_FOLDERS;
      body.split(/[\\\/]+/).forEach(function (part) {
        var target = exclude ? excl : terms;
        if (part && target.indexOf(part) < 0) target.push(part);
      });
      if (terms.length >= 20) break;
    }
    if (exts.length > 0 && q.kind === KIND_FOLDERS) q.kind = KIND_ALL;
    q.terms = terms;
    q.exts = exts;
    q.alts = terms.map(function (t) { return synonyms ? synonyms.expand(t) : [t]; });
    excl.forEach(function (e) {
      (synonyms ? synonyms.expand(e) : [e]).forEach(function (a) { if (q.excludes.indexOf(a) < 0) q.excludes.push(a); });
    });
    return q;
  }

  function ymd(y, m, d) { return new Date(y, m - 1, d); }
  function addDays(dt, n) { return new Date(dt.getFullYear(), dt.getMonth(), dt.getDate() + n, dt.getHours(), dt.getMinutes(), dt.getSeconds()); }
  function addMonths(dt, n) {
    var y = dt.getFullYear(), m = dt.getMonth() + n;
    var last = new Date(y, m + 1, 0).getDate();
    return new Date(y, m, Math.min(dt.getDate(), last), dt.getHours(), dt.getMinutes(), dt.getSeconds());
  }

  function tryDate(q, s, now) {
    var openEnd = false, openStart = false, m;
    if ((m = /^(.+?)(以降|から|以後)$/.exec(s))) { openEnd = true; s = m[1]; }
    else if ((m = /^(.+?)(以前|まで)$/.exec(s))) { openStart = true; s = m[1]; }
    var r = resolveDate(s, now);
    if (!r) return false;
    if (openEnd) { r.to = null; r.label += ' 以降'; }
    if (openStart) { r.from = null; r.label += ' 以前'; }
    var min = r.from ? r.from.getTime() : 0, max = r.to ? r.to.getTime() : 0;
    if (min > q.minTime) q.minTime = min;
    if (max > 0 && (q.maxTime === 0 || max < q.maxTime)) q.maxTime = max;
    q.dateLabel = q.dateLabel ? q.dateLabel + ' / ' + r.label : r.label;
    return true;
  }

  function resolveDate(s, now) {
    var today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    var monday = addDays(today, -((today.getDay() + 6) % 7));
    var month1 = new Date(today.getFullYear(), today.getMonth(), 1);
    var jan1 = new Date(today.getFullYear(), 0, 1);
    var R = function (from, to, label) { return { from: from, to: to, label: label }; };
    switch (s) {
      case '今日': case 'きょう': case 'today': return R(today, addDays(today, 1), '今日');
      case '昨日': case 'きのう': case 'yesterday': return R(addDays(today, -1), today, '昨日');
      case '今週': case 'こんしゅう': case 'week': case 'thisweek': return R(monday, null, '今週');
      case '先週': case 'せんしゅう': case 'lastweek': return R(addDays(monday, -7), monday, '先週');
      case '今月': case 'こんげつ': case 'month': case 'thismonth': return R(month1, null, '今月');
      case '先月': case 'せんげつ': case 'lastmonth': return R(addMonths(month1, -1), month1, '先月');
      case '今年': case 'ことし': case 'year': case 'thisyear': return R(jan1, null, '今年');
      case '去年': case '昨年': case 'きょねん': case 'さくねん': case 'lastyear': return R(new Date(jan1.getFullYear() - 1, 0, 1), jan1, '去年');
    }
    var m = /^(\d{1,3})(日|日間|d|days?|週|週間|w|weeks?|[かゖゕけ箇]月|か月間|m|months?|年|年間|y|years?)$/.exec(s);
    if (m) {
      var n = +m[1], u = m[2][0];
      if (n <= 0) return null;
      if (u === '日' || u === 'd') return R(addDays(now, -n), null, '直近' + n + '日');
      if (u === '週' || u === 'w') return R(addDays(now, -7 * n), null, '直近' + n + '週間');
      if (u === '年' || u === 'y') return R(addMonths(now, -12 * n), null, '直近' + n + '年');
      return R(addMonths(now, -n), null, '直近' + n + 'か月');
    }
    var fiscal = function (y) { return y < 1900 || y > 2200 ? null : R(ymd(y, 4, 1), ymd(y + 1, 4, 1), y + '年度'); };
    if ((m = /^(\d{4})年度$/.exec(s))) return fiscal(+m[1]);
    if ((m = /^(令和|r|平成|h)(元|\d{1,2})年?(度)?$/.exec(s))) {
      var en = m[2] === '元' ? 1 : +m[2], reiwa = m[1] === '令和' || m[1] === 'r';
      if (en < 1 || (!reiwa && en > 31)) return null;
      var ey = (reiwa ? 2018 : 1988) + en;
      return m[3] ? fiscal(ey) : R(ymd(ey, 1, 1), ymd(ey + 1, 1, 1), ey + '年');
    }
    if ((m = /^(\d{4})年?$/.exec(s))) {
      var y = +m[1];
      return y < 1900 || y > 2200 ? null : R(ymd(y, 1, 1), ymd(y + 1, 1, 1), y + '年');
    }
    if ((m = /^(\d{4})(?:[-\/.年](\d{1,2})月?|(\d{2}))$/.exec(s))) {
      var my = +m[1], mo = +(m[2] || m[3]);
      return my < 1900 || my > 2200 || mo < 1 || mo > 12 ? null : R(ymd(my, mo, 1), ymd(my, mo + 1, 1), my + '年' + mo + '月');
    }
    if ((m = /^(\d{4})(?:[-\/.年](\d{1,2})[-\/.月](\d{1,2})日?|(\d{2})(\d{2}))$/.exec(s))) {
      var dy = +m[1], dm = +(m[2] || m[4]), dd = +(m[3] || m[5]);
      if (dy < 1900 || dy > 2200 || dm < 1 || dm > 12 || dd < 1 || dd > new Date(dy, dm, 0).getDate()) return null;
      return R(ymd(dy, dm, dd), ymd(dy, dm, dd + 1), dy + '/' + dm + '/' + dd);
    }
    if ((m = /^(\d{1,2})月$/.exec(s))) {
      var om = +m[1];
      if (om < 1 || om > 12) return null;
      var oy = om > today.getMonth() + 1 ? today.getFullYear() - 1 : today.getFullYear();
      return R(ymd(oy, om, 1), ymd(oy, om + 1, 1), oy + '年' + om + '月');
    }
    return null;
  }

  // ------------------------------------------------------------------
  // パス
  // ------------------------------------------------------------------
  function trimPath(p) {
    if (!p) return p || '';
    var end = p.length;
    while (end > 1 && (p[end - 1] === '\\' || p[end - 1] === '/')) {
      if (end === 3 && p[1] === ':') break;
      end--;
    }
    var r = p.slice(0, end);
    if (r.length === 2 && r[1] === ':') r += '\\';
    return r;
  }

  function lastSegment(p) {
    p = trimPath(p);
    var i = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'));
    if (i < 0 || i === p.length - 1) return p;
    return p.slice(i + 1);
  }

  function parentPath(p) {
    p = trimPath(p);
    var i = Math.max(p.lastIndexOf('\\'), p.lastIndexOf('/'));
    if (i <= 0 || i === p.length - 1) return null;
    if (p.indexOf('\\\\') === 0 && p.indexOf('\\', 2) === i) return null;
    return trimPath(p.slice(0, i + 1));
  }

  function isUnder(path, rootPath) {
    path = trimPath(path);
    rootPath = trimPath(rootPath);
    if (path.toLowerCase().indexOf(rootPath.toLowerCase()) !== 0) return false;
    if (path.length === rootPath.length) return true;
    var last = rootPath[rootPath.length - 1];
    if (last === '\\' || last === '/') return true;
    var c = path[rootPath.length];
    return c === '\\' || c === '/';
  }

  function prettyPath(p, maxChars) {
    if (!p) return '';
    var parts = p.replace(/^[\\\/]+/, '').split(/[\\\/]+/).filter(Boolean);
    if (parts.length === 0) return p;
    var start = 0, total = 0;
    for (var i = parts.length - 1; i >= 0; i--) {
      total += parts[i].length + 3;
      if (total > maxChars && i < parts.length - 1) { start = i + 1; break; }
    }
    var shown = parts.slice(start).join('  ›  ');
    return start > 0 ? '…  ›  ' + shown : shown;
  }

  function tryParsePath(text) {
    if (!text) return null;
    var s = text.trim().replace(/^["'<「“]+|["'>」”]+$/g, '').trim();
    if (/^file:/i.test(s)) {
      try {
        var u = new URL(s);
        s = u.host ? '\\\\' + u.host + decodeURIComponent(u.pathname).replace(/\//g, '\\')
                   : decodeURIComponent(u.pathname).replace(/^\//, '').replace(/\//g, '\\');
      } catch (e) { return null; }
    }
    if (s.length > 2 && s.indexOf('\\\\') === 0 && s[2] !== '\\' && s[2] !== '?') return trimPath(s.replace(/\//g, '\\'));
    if (/^[A-Za-z]:[\\\/]/.test(s)) return trimPath(s.replace(/\//g, '\\'));
    return null;
  }

  // ------------------------------------------------------------------
  // インデックス (親は必ず子より前: parent[i] < i)
  // ------------------------------------------------------------------
  function FileIndex(o) {
    this.count = o.name.length;
    this.parent = o.parent;
    this.name = o.name;
    this.key = o.key || o.name.map(normKey);
    this.mtime = o.mtime;
    this.flags = o.flags;
    this.roots = o.roots || [];
    this.sep = o.sep || '\\';
    this.builtAt = o.builtAt || Date.now();
    this.depth = new Uint16Array(this.count);
    for (var i = 0; i < this.count; i++) {
      var p = this.parent[i];
      this.depth[i] = p < 0 ? 0 : Math.min(65535, this.depth[p] + 1);
    }
    this._anc = null;
  }

  FileIndex.prototype.isDir = function (i) { return (this.flags[i] & FLAG_DIR) !== 0; };
  FileIndex.prototype.isRoot = function (i) { return (this.flags[i] & FLAG_ROOT) !== 0; };

  FileIndex.prototype.fullPath = function (i) {
    if (i < 0 || i >= this.count) return null;
    var chain = [];
    for (var cur = i; cur >= 0; cur = this.parent[cur]) chain.push(this.name[cur]);
    var out = '';
    for (var k = chain.length - 1; k >= 0; k--) {
      if (out.length > 0) {
        var last = out[out.length - 1];
        if (last !== '\\' && last !== '/') out += this.sep;
      }
      out += chain[k];
    }
    return out;
  };

  function isBoundary(s, pos) {
    var prev = s[pos - 1], cur = s[pos];
    if (' _-.()[]{}（）【】「」『』・,、。+&#~'.indexOf(prev) >= 0) return true;
    var pd = prev >= '0' && prev <= '9', cd = cur >= '0' && cur <= '9';
    if (pd !== cd) return true;
    return (prev.charCodeAt(0) < 0x80) !== (cur.charCodeAt(0) < 0x80);
  }

  function firstMatch(key, alts) {
    for (var j = 0; j < alts.length; j++) {
      var pos = key.indexOf(alts[j]);
      if (pos >= 0) return { term: alts[j], pos: pos };
    }
    return null;
  }

  FileIndex.prototype.score = function (i, key, alts, own, all, now, preferFolders) {
    var s = 0, matched = 0;
    for (var t = 0; t < alts.length; t++) {
      if ((own & (1 << t)) === 0) { s += 1.5; continue; }
      var f = firstMatch(key, alts[t]);
      if (!f) continue;
      s += 10;
      matched += f.term.length;
      if (f.pos === 0) s += 6;
      else if (isBoundary(key, f.pos)) s += 3;
    }
    if (own === all) s += 6;
    var baseLen = key.length;
    if (!this.isDir(i)) {
      var dot = key.lastIndexOf('.');
      if (dot > 0) baseLen = dot;
    }
    if (baseLen > 0) s += 10 * Math.min(1, matched / baseLen);
    if (this.isDir(i)) s += preferFolders ? 18 : 3; // 保存先を選ぶときはフォルダを上に
    s -= this.depth[i] * 0.35;
    var mt = this.mtime[i];
    if (mt > 0) {
      var age = now - mt;
      if (age < 30 * DAY) s += 2;
      else if (age < 365 * DAY) s += 1;
    }
    return s;
  };

  FileIndex.prototype.passFilters = function (i, key, q) {
    var dir = this.isDir(i);
    if (q.kind === KIND_FOLDERS && !dir) return false;
    if (q.kind === KIND_FILES && dir) return false;
    if (q.exts.length > 0) {
      if (dir) return false;
      var dot = key.lastIndexOf('.');
      if (dot < 0) return false;
      var ext = key.slice(dot + 1);
      if (!q.exts.some(function (e) { return ext.indexOf(e) === 0; })) return false;
    }
    if (q.minTime > 0 || q.maxTime > 0) {
      var mt = this.mtime[i];
      if (!(mt > 0)) return false;
      if (q.minTime > 0 && mt < q.minTime) return false;
      if (q.maxTime > 0 && mt >= q.maxTime) return false;
    }
    return true;
  };

  /**
   * すべての語を「名前 or 親フォルダ名」に含み、少なくとも 1 語は自分の名前に含む項目を返す。
   * 語がなく絞り込み (.xlsx / @今週) だけなら、条件に合うものを新しい順に返す。
   */
  FileIndex.prototype.search = function (q, scope, boost, limit, now) {
    var heap = new TopK(limit);
    var n = this.count;
    var alts = q.alts.slice(0, 24);
    var k = alts.length, filterOnly = k === 0, hasFilters = q.hasFilters();
    if ((filterOnly && !hasFilters) || n === 0 || scope >= n) return heap.sorted();
    var excl = q.excludes;
    var all = (1 << k) - 1;
    var start = scope >= 0 ? scope : 0;
    now = now || Date.now();
    if (!this._anc || this._anc.length < n) this._anc = new Int32Array(n);
    var anc = this._anc;
    for (var i = start; i < n; i++) {
      var p = this.parent[i];
      var inherited = p >= start ? anc[p] : 0;
      if (scope >= 0) {
        if (i === scope) { anc[i] = SCOPE_BIT; continue; }
        if ((inherited & SCOPE_BIT) === 0) { anc[i] = 0; continue; }
      }
      if ((inherited & EXCL_BIT) !== 0) { anc[i] = inherited; continue; }
      var key = this.key[i];
      var own = 0;
      for (var t = 0; t < k; t++) {
        var a = alts[t];
        for (var j = 0; j < a.length; j++) if (key.indexOf(a[j]) >= 0) { own |= 1 << t; break; }
      }
      var m = inherited | own;
      for (var e = 0; e < excl.length; e++) if (key.indexOf(excl[e]) >= 0) { m |= EXCL_BIT; break; }
      anc[i] = m;
      if ((m & EXCL_BIT) !== 0) continue;
      if (!filterOnly && (own === 0 || (m & all) !== all)) continue;
      if (hasFilters && !this.passFilters(i, key, q)) continue;
      if (filterOnly) {
        if (this.parent[i] >= 0) heap.offer(this.mtime[i], i); // 新しい順
        continue;
      }
      var sc = this.score(i, key, alts, own, all, now, q.preferFolders);
      if (boost && boost.has(i)) sc += boost.get(i);
      heap.offer(sc, i);
    }
    return heap.sorted();
  };

  var collator = (typeof Intl !== 'undefined') ? new Intl.Collator('ja', { numeric: true, sensitivity: 'base' }) : null;

  FileIndex.prototype.children = function (dir, max) {
    var list = [];
    if (dir < 0 || dir >= this.count) return list;
    for (var i = dir + 1; i < this.count; i++) if (this.parent[i] === dir) list.push(i);
    var self = this;
    list.sort(function (a, b) {
      var da = self.isDir(a), db = self.isDir(b);
      if (da !== db) return da ? -1 : 1;
      return collator ? collator.compare(self.name[a], self.name[b]) : (self.name[a] < self.name[b] ? -1 : 1);
    });
    return list.slice(0, max);
  };

  /** フルパス → ノード番号 (まとめて 1 パスで解決)。戻り値のキーは小文字。 */
  FileIndex.prototype.resolve = function (paths) {
    var result = new Map();
    if (!paths || paths.length === 0 || this.count === 0) return result;
    var wanted = new Set(), lastKeys = new Set();
    paths.forEach(function (raw) {
      var p = trimPath(raw);
      wanted.add(p.toLowerCase());
      lastKeys.add(normKey(lastSegment(p)));
    });
    for (var i = 0; i < this.count; i++) {
      if (this.parent[i] < 0) {
        var rp = trimPath(this.name[i]).toLowerCase();
        if (wanted.has(rp)) result.set(rp, i);
        continue;
      }
      if (!lastKeys.has(this.key[i])) continue;
      var fp = this.fullPath(i).toLowerCase();
      if (wanted.has(fp)) result.set(fp, i);
    }
    return result;
  };

  FileIndex.prototype.find = function (path) {
    var r = this.resolve([path]);
    var v = r.get(trimPath(path).toLowerCase());
    return v === undefined ? -1 : v;
  };

  function TopK(cap) {
    this.cap = Math.max(1, cap);
    this.s = [];
    this.n = [];
  }
  TopK.prototype.swap = function (a, b) {
    var ts = this.s[a]; this.s[a] = this.s[b]; this.s[b] = ts;
    var tn = this.n[a]; this.n[a] = this.n[b]; this.n[b] = tn;
  };
  TopK.prototype.offer = function (score, node) {
    var s = this.s, i;
    if (s.length < this.cap) {
      s.push(score); this.n.push(node);
      i = s.length - 1;
      while (i > 0) {
        var p = (i - 1) >> 1;
        if (s[p] <= s[i]) break;
        this.swap(i, p); i = p;
      }
    } else if (score > s[0]) {
      s[0] = score; this.n[0] = node;
      i = 0;
      for (;;) {
        var l = i * 2 + 1, r = l + 1, m = i;
        if (l < s.length && s[l] < s[m]) m = l;
        if (r < s.length && s[r] < s[m]) m = r;
        if (m === i) break;
        this.swap(i, m); i = m;
      }
    }
  };
  TopK.prototype.sorted = function () {
    var out = [];
    for (var i = 0; i < this.s.length; i++) out.push({ node: this.n[i], score: this.s[i] });
    out.sort(function (a, b) { return b.score - a.score || a.node - b.node; });
    return out;
  };

  // ------------------------------------------------------------------
  // よく使う / ピン留め
  // ------------------------------------------------------------------
  var PIN_BONUS = 25;

  function frecency(e, now) {
    var hours = (now - e.last) / 3600000;
    var recency = hours < 4 ? 4 : hours < 24 ? 2.5 : hours < 24 * 7 ? 1.6 : hours < 24 * 30 ? 1 : 0.5;
    return Math.min(40, Math.log(1 + e.count) * recency * 6);
  }

  function UsageStore(data) {
    this.history = new Map();
    this.pins = [];
    this.version = 0;
    if (data) this.load(data);
  }
  UsageStore.prototype.load = function (data) {
    var self = this;
    (data.history || []).forEach(function (e) { self.history.set(e.path.toLowerCase(), e); });
    this.pins = (data.pins || []).slice();
    this.version++;
  };
  UsageStore.prototype.toJSON = function () {
    return { history: Array.from(this.history.values()), pins: this.pins };
  };
  UsageStore.prototype.record = function (path, isDir, now) {
    path = trimPath(path);
    var k = path.toLowerCase();
    var e = this.history.get(k);
    if (!e) { e = { path: path, isDir: isDir, count: 0, last: 0 }; this.history.set(k, e); }
    e.isDir = isDir;
    e.count++;
    e.last = now || Date.now();
    this.version++;
  };
  UsageStore.prototype.isPinned = function (path) {
    var k = trimPath(path).toLowerCase();
    return this.pins.some(function (p) { return p.path.toLowerCase() === k; });
  };
  UsageStore.prototype.togglePin = function (path, isDir) {
    path = trimPath(path);
    var k = path.toLowerCase();
    var idx = this.pins.findIndex(function (p) { return p.path.toLowerCase() === k; });
    this.version++;
    if (idx >= 0) { this.pins.splice(idx, 1); return false; }
    this.pins.push({ path: path, isDir: isDir });
    return true;
  };
  UsageStore.prototype.topHistory = function (n, now) {
    now = now || Date.now();
    return Array.from(this.history.values())
      .sort(function (a, b) { return frecency(b, now) - frecency(a, now); })
      .slice(0, n);
  };
  UsageStore.prototype.clearHistory = function () { this.history.clear(); this.version++; };
  UsageStore.prototype.boostByPath = function (now) {
    now = now || Date.now();
    var map = new Map();
    this.history.forEach(function (e, k) { map.set(k, frecency(e, now)); });
    this.pins.forEach(function (p) {
      var k = p.path.toLowerCase();
      map.set(k, (map.get(k) || 0) + PIN_BONUS);
    });
    return map;
  };

  // ------------------------------------------------------------------
  // 入力 → 結果リスト
  // ------------------------------------------------------------------
  var MAX_RESULTS = 80;
  var MAX_CHILDREN = 400;

  function formatDate(ms) {
    if (!ms) return null;
    var d = new Date(ms);
    var mm = ('0' + (d.getMonth() + 1)).slice(-2), dd = ('0' + d.getDate()).slice(-2);
    return d.getFullYear() + '/' + mm + '/' + dd;
  }

  function highlight(name, key, alts) {
    if (!name || !key || name.length !== key.length || alts.length === 0) return null;
    var marks = new Array(name.length);
    var any = false;
    alts.forEach(function (a) {
      var f = firstMatch(key, a);
      if (!f) return;
      for (var i = f.pos; i < f.pos + f.term.length; i++) marks[i] = true;
      any = true;
    });
    if (!any) return null;
    var spans = [], start = -1;
    for (var i = 0; i <= marks.length; i++) {
      var on = i < marks.length && marks[i];
      if (on && start < 0) start = i;
      if (!on && start >= 0) { spans.push(start, i - start); start = -1; }
    }
    return spans;
  }

  function QueryEngine(host, usage) {
    this.host = host;   // { roots(): string[], commands(): [{glyph,title,subtitle,run,keepOpen}] }
    this.usage = usage;
    this._boostIndex = null;
    this._boostVersion = -1;
    this._boost = new Map();
    this._pathToNode = new Map();
  }

  QueryEngine.prototype.ensureBoost = function (index) {
    if (this._boostIndex === index && this._boostVersion === this.usage.version) return;
    var weights = this.usage.boostByPath();
    var resolved = index.resolve(Array.from(weights.keys()));
    var map = new Map();
    resolved.forEach(function (node, k) { if (weights.has(k)) map.set(node, weights.get(k)); });
    this._boost = map;
    this._pathToNode = resolved;
    this._boostIndex = index;
    this._boostVersion = this.usage.version;
  };

  QueryEngine.prototype.nodeOf = function (index, path) {
    this.ensureBoost(index);
    var v = this._pathToNode.get(trimPath(path).toLowerCase());
    return v === undefined ? index.find(path) : v;
  };

  QueryEngine.prototype.fromNode = function (index, node, withParent) {
    var path = index.fullPath(node);
    var item = {
      kind: 'entry', title: index.name[node], path: path, isDir: index.isDir(node), node: node,
      mtime: index.mtime[node], pinned: this.usage.isPinned(path), subtitle: null, badge: null
    };
    if (index.isRoot(node)) item.subtitle = '検索対象';
    else if (withParent) item.subtitle = prettyPath(index.fullPath(index.parent[node]), 90);
    item.badge = formatDate(item.mtime);
    return item;
  };

  QueryEngine.prototype.compute = function (text, scope, index, preferFolders) {
    var t = (text || '').trim();
    if (t[0] === '>' || t[0] === '＞') return this.commands(t.slice(1));
    if (t === '?' || t === '？') return this.help();
    var asPath = tryParsePath(t);
    if (asPath) return this.pathItems(asPath, index);
    var q = parseQuery(t, this.host.synonyms ? this.host.synonyms() : null, new Date());
    q.preferFolders = !!preferFolders;
    if (q.isEmpty()) return scope >= 0 ? this.childItems(index, scope) : this.home(index, preferFolders);
    this.ensureBoost(index);
    var hits = index.search(q, scope, this._boost, MAX_RESULTS);
    var self = this;
    return hits.map(function (h) {
      var item = self.fromNode(index, h.node, true);
      item.highlights = highlight(index.name[h.node], index.key[h.node], q.alts);
      return item;
    });
  };

  QueryEngine.prototype.help = function () {
    var ex = function (example, description) {
      return { kind: 'command', glyph: 'search', title: example, subtitle: description, action: 'query:' + example, keepOpen: true, badge: '例' };
    };
    return [
      ex('見積 2024', 'スペースで区切ると、すべてを含むものに絞り込み (親フォルダの名前も対象)'),
      ex('見積 .xlsx', '拡張子で絞り込み (.xls だけで .xlsx / .xlsm も)。.* はファイルだけ'),
      ex('営業/', '末尾に / を付けるとフォルダだけ (/ だけなら、すべてのフォルダ)'),
      ex('.pdf @今週', '更新日で絞り込み: @今日 @昨日 @今週 @先週 @今月 @先月 @今年 @去年'),
      ex('@7日', '最近 N 日に更新したもの (@2週 @3か月 @1年 も)。新しい順に並びます'),
      ex('議事録 @2024-05', '年・月・日で指定: @2024 @2024-05 @2024-05-12 @2024年度 @r6'),
      ex('契約 @2024-04以降', '以降 / 以前 を付けると、その日より後 / 前'),
      ex('議事録 -旧', '- を付けた言葉を含むものを除外'),
      ex('R6 見積', '和暦と西暦は同じものとして探します (2024 = 令和6 = R6)'),
      ex('見積もり', '表記ゆれ辞書: 見積 / 見積り / お見積 なども探します (> 設定ファイル で追加)'),
      ex('>', 'コマンド (再スキャン・見た目の設定など)')
    ];
  };

  QueryEngine.prototype.home = function (index, foldersOnly) {
    var items = [];
    var roots = this.host.roots();
    if (roots.length === 0) {
      items.push(cmd('add', 'フォルダを選んで検索対象に追加…', 'ファイルサーバーの共有フォルダなどを選んでください', 'pickRoot', true));
      return items;
    }
    if (index.count > 0) this.ensureBoost(index);
    var map = this._boostIndex === index ? this._pathToNode : new Map();
    var self = this;
    var pins = this.usage.pins;
    if (pins.length > 0) {
      items.push({ kind: 'header', title: 'ピン留め' });
      pins.forEach(function (p) { items.push(self.fromUsage(p, map, true)); });
    }
    var recent = [];
    var top = this.usage.topHistory(40);
    for (var i = 0; i < top.length && recent.length < 10; i++) {
      if (this.usage.isPinned(top[i].path) || (foldersOnly && !top[i].isDir)) continue;
      recent.push(this.fromUsage(top[i], map, false));
    }
    if (recent.length > 0) {
      items.push({ kind: 'header', title: 'よく使う' });
      items.push.apply(items, recent);
    }
    items.push({ kind: 'header', title: '検索対象' });
    roots.forEach(function (r) {
      var node = map.get(r.toLowerCase());
      if (node === undefined) {
        node = -1;
        for (var i = 0; i < index.count && i < 64; i++) {
          if (index.parent[i] < 0 && index.name[i].toLowerCase() === r.toLowerCase()) { node = i; break; }
        }
      }
      items.push({ kind: 'entry', title: r, path: r, isDir: true, node: node, subtitle: 'Tab で中を表示', pinned: self.usage.isPinned(r) });
    });
    return items;
  };

  QueryEngine.prototype.fromUsage = function (e, map, pinned) {
    var node = map.get(e.path.toLowerCase());
    return {
      kind: 'entry', title: lastSegment(e.path), subtitle: prettyPath(parentPath(e.path) || '', 90),
      path: e.path, isDir: e.isDir, node: node === undefined ? -1 : node,
      pinned: pinned || this.usage.isPinned(e.path)
    };
  };

  QueryEngine.prototype.childItems = function (index, scope) {
    var self = this;
    return index.children(scope, MAX_CHILDREN).map(function (c) { return self.fromNode(index, c, false); });
  };

  QueryEngine.prototype.pathItems = function (path, index) {
    var items = [];
    var hasExt = /\.[^\\\/.]+$/.test(lastSegment(path));
    var open = {
      kind: 'entry', title: lastSegment(path), subtitle: path, path: path, isDir: !hasExt, node: -1,
      badge: '貼り付けたパス', pinned: this.usage.isPinned(path)
    };
    if (index.count > 0) {
      var node = this.nodeOf(index, path);
      if (node >= 0) {
        open.node = node;
        open.isDir = index.isDir(node);
        open.badge = 'Tab で中を表示';
      }
    }
    items.push(open);
    var covered = this.host.roots().some(function (r) { return isUnder(path, r); });
    if (!covered) items.push(cmd('add', 'このフォルダを検索対象に追加', path, 'addRoot:' + path, true));
    return items;
  };

  QueryEngine.prototype.commands = function (filter) {
    var main = this.host.commands();
    var q = parseQuery(filter);
    if (q.terms.length === 0) return main;
    var all = main.concat(this.host.appearanceCommands ? this.host.appearanceCommands() : []);
    var matched = all.filter(function (item) {
      var key = normKey(item.title + ' ' + (item.subtitle || ''));
      return q.terms.every(function (term) { return key.indexOf(term) >= 0; });
    });
    // 名前が入力した語で始まるコマンドを先に (「>設定」なら「設定ファイルを開く」が一番上)
    var starts = function (item) { return normKey(item.title).indexOf(q.terms[0]) === 0; };
    return matched.filter(starts).concat(matched.filter(function (x) { return !starts(x); }));
  };

  function cmd(glyph, title, subtitle, action, keepOpen) {
    return { kind: 'command', glyph: glyph, title: title, subtitle: subtitle, action: action, keepOpen: !!keepOpen };
  }

  // ------------------------------------------------------------------
  // サンプルのファイルサーバー (架空の会社)
  // ------------------------------------------------------------------
  function mulberry32(a) {
    return function () {
      a |= 0; a = a + 0x6D2B79F5 | 0;
      var t = Math.imul(a ^ a >>> 15, 1 | a);
      t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
      return ((t ^ t >>> 14) >>> 0) / 4294967296;
    };
  }

  function IndexBuilder(sep) {
    this.parent = []; this.name = []; this.mtime = []; this.flags = []; this.roots = []; this.sep = sep || '\\';
  }
  IndexBuilder.prototype.add = function (p, name, mtime, isDir, isRoot) {
    this.parent.push(p);
    this.name.push(name);
    this.mtime.push(mtime || 0);
    this.flags.push((isDir ? FLAG_DIR : 0) | (isRoot ? FLAG_ROOT : 0));
    if (isRoot) this.roots.push(name);
    return this.name.length - 1;
  };
  IndexBuilder.prototype.build = function () {
    return new FileIndex({
      parent: Int32Array.from(this.parent), name: this.name, mtime: this.mtime,
      flags: Uint8Array.from(this.flags), roots: this.roots, sep: this.sep
    });
  };

  function buildSampleServer(nowMs) {
    var rnd = mulberry32(20261024);
    var pick = function (arr) { return arr[Math.floor(rnd() * arr.length)]; };
    var b = new IndexBuilder('\\');
    var now = nowMs || Date.now();
    var thisYear = new Date(now).getFullYear();
    var years = [];
    for (var y = thisYear - 6; y <= thisYear; y++) years.push(y);
    var at = function (year, month, day) {
      var t = new Date(year, (month || 1) - 1, day || 1 + Math.floor(rnd() * 27), 9 + Math.floor(rnd() * 9), Math.floor(rnd() * 60)).getTime();
      return Math.min(t, now - 3600000);
    };
    var mm = function (m) { return ('0' + m).slice(-2); };
    var curMonth = new Date(now).getMonth() + 1;
    var future = function (y, m) { return y > thisYear || (y === thisYear && m > curMonth); };

    var companies = ['青葉商事', '北斗工業', 'みなと電機', 'さくら物流', 'あおい建設', 'ひかり製薬', 'つばさ食品', 'かえで不動産',
      'いずみ精機', 'ほのか印刷', 'はやぶさ運輸', 'こだま技研', 'しらかば産業', 'わかば医療', 'やまびこ通信', 'あさひ化成',
      'みどり電設', 'あけぼの製作所', 'なぎさ水産', 'ことぶき商会', 'すずらん化粧品', 'もみじ観光', 'ひばり教育', 'おおぞら空調'];
    var root = b.add(-1, '\\\\fileserver\\share', 0, true, true);
    var dir = function (p, name, t) { return b.add(p, name, t || at(pick(years), 1 + Math.floor(rnd() * 12)), true); };
    var file = function (p, name, t) { return b.add(p, name, t || at(pick(years), 1 + Math.floor(rnd() * 12)), false); };

    // 01_総務部
    var soumu = dir(root, '01_総務部');
    var kitei = dir(soumu, '規程類');
    ['就業規則.docx', '就業規則_改定案_新旧対照表.xlsx', '経費精算規程.pdf', '出張旅費規程.pdf', '情報セキュリティ規程.pdf',
      '育児介護休業規程.docx', '文書管理規程.docx'].forEach(function (f) { file(kitei, f); });
    var kitei_old = dir(kitei, '旧');
    ['就業規則_2019.docx', '就業規則_2021.docx', '経費精算規程_旧.pdf'].forEach(function (f) { file(kitei_old, f); });
    var bihin = dir(soumu, '備品管理');
    file(bihin, '備品台帳.xlsx'); file(bihin, 'PC貸与一覧.xlsx'); file(bihin, '複合機リース契約書.pdf');
    var gijiroku = dir(soumu, '議事録');
    years.slice(-3).forEach(function (y) {
      var yd = dir(gijiroku, y + '年度', at(y, 4, 1));
      for (var m = 1; m <= 12; m++) {
        if (rnd() < 0.25) continue;
        if (future(y, m)) continue;
        file(yd, '定例会議議事録_' + y + '-' + mm(m) + '-' + mm(5 + Math.floor(rnd() * 20)) + '.docx', at(y, m));
      }
    });
    var shanaiho = dir(soumu, '社内報');
    years.forEach(function (y) {
      if (!future(y, 7)) file(shanaiho, '社内報_' + y + '夏号.pdf', at(y, 7));
      if (!future(y, 12)) file(shanaiho, '社内報_' + y + '冬号.pdf', at(y, 12));
    });

    // 02_営業部
    var eigyo = dir(root, '02_営業部');
    var kokyaku = dir(eigyo, '顧客');
    companies.forEach(function (c, ci) {
      var cd = dir(kokyaku, (ci + 1 < 10 ? '0' : '') + (ci + 1) + '_' + c);
      file(cd, c + '_会社概要.pdf');
      file(cd, c + '_取引先登録票.xlsx');
      var main = ci < 2; // 先頭の 2 社は全年度そろえる (デモの検索例が必ずヒットするように)
      years.forEach(function (y) {
        if (!main && rnd() < 0.25) return;
        var yd = dir(cd, y + '年度', at(y, 4, 1));
        [['見積', '見積書', 'xlsx'], ['契約', '契約書', 'pdf'], ['請求', '請求書', 'pdf'], ['提案', '提案書', 'pptx']].forEach(function (kind) {
          if (!main && rnd() < 0.15) return;
          var kd = dir(yd, kind[0], at(y, 4, 2));
          var n = 2 + Math.floor(rnd() * 6);
          for (var i = 0; i < n; i++) {
            var m = 1 + Math.floor(rnd() * (y === thisYear ? curMonth : 12));
            var suffix = rnd() < 0.15 ? '_修正版' : rnd() < 0.1 ? '_最終' : '';
            var label = kind[1];
            if (kind[0] === '見積') label = pick(['見積書', '見積書', '御見積書', '見積り']); // 表記ゆれ
            file(kd, label + '_' + c + '_' + y + '-' + mm(m) + suffix + '.' + kind[2], at(y, m));
          }
          if (kind[0] === '見積' && rnd() < 0.5) file(kd, '単価表_' + y + '.xlsx', at(y, 4));
        });
        if (rnd() < 0.6) {
          var md = dir(yd, '打合せメモ', at(y, 5));
          for (var i = 0; i < 2 + Math.floor(rnd() * 5); i++) {
            var m = 1 + Math.floor(rnd() * (y === thisYear ? curMonth : 12));
            file(md, pick(['打合せ', '打ち合わせ', '打合せメモ']) + '_' + c + '_' + y + mm(m) + mm(1 + Math.floor(rnd() * 27)) + '.docx', at(y, m));
          }
        }
      });
    });
    var uriage = dir(eigyo, '売上報告');
    years.forEach(function (y) {
      var yd = dir(uriage, y + '年度', at(y, 4, 1));
      for (var m = 1; m <= 12; m++) if (!future(y, m)) file(yd, '月次売上報告_' + y + '-' + mm(m) + '.xlsx', at(y, m, 28));
    });
    var eikai = dir(eigyo, '営業会議');
    years.slice(-2).forEach(function (y) {
      for (var m = 1; m <= 12; m++) if (rnd() < 0.6 && !future(y, m)) file(eikai, '営業会議資料_' + y + '-' + mm(m) + '.pptx', at(y, m));
    });
    file(eigyo, '顧客リスト_最新.xlsx', now - 2 * DAY);
    file(eigyo, '【至急】価格改定のお知らせ_ドラフト.docx', now - 5 * DAY);

    // 03_経理部
    var keiri = dir(root, '03_経理部');
    var kessan = dir(keiri, '決算');
    years.forEach(function (y) {
      var yd = dir(kessan, y + '年度決算', at(y + 1, 5, 20));
      ['貸借対照表.xlsx', '損益計算書.xlsx', '決算説明資料.pptx', '監査対応メモ.docx'].forEach(function (f) { file(yd, y + '年度_' + f, at(y + 1, 5)); });
    });
    var shiharai = dir(keiri, '支払');
    years.slice(-3).forEach(function (y) {
      var yd = dir(shiharai, String(y), at(y, 1, 1));
      for (var m = 1; m <= 12; m++) {
        if (future(y, m)) continue;
        var md = dir(yd, mm(m) + '月', at(y, m, 1));
        for (var i = 0; i < 4; i++) file(md, '支払明細_' + pick(companies) + '_' + y + mm(m) + '.pdf', at(y, m));
      }
    });
    var yosan = dir(keiri, '予算');
    years.slice(-4).forEach(function (y) {
      var era = 'R' + (y - 2018);
      var yd = dir(yosan, era + '年度', at(y - 1, 2, 1)); // 和暦のフォルダ名 (2024 でも見つかる)
      file(yd, era + '年度予算_全社.xlsx', at(y - 1, 3));
      file(yd, '令和' + (y - 2018) + '年度_部門別予算案.xlsx', at(y - 1, 2));
    });
    var keihi = dir(keiri, '経費精算');
    file(keihi, '経費精算書_テンプレート.xlsx'); file(keihi, '経費精算の手引き.pdf');

    // 04_人事部
    var jinji = dir(root, '04_人事部');
    var saiyo = dir(jinji, '採用');
    years.slice(-3).forEach(function (y) {
      var yd = dir(saiyo, y + '新卒', at(y - 1, 3, 1));
      file(yd, '採用計画_' + y + '.xlsx', at(y - 1, 3)); file(yd, '会社説明会資料_' + y + '.pptx', at(y - 1, 3)); file(yd, '面接評価シート.xlsx', at(y - 1, 5));
    });
    var kenshu = dir(jinji, '研修資料');
    ['新入社員研修_ビジネスマナー.pptx', 'ハラスメント防止研修.pptx', '情報セキュリティ研修.pptx', '管理職研修_評価者訓練.pptx'].forEach(function (f) { file(kenshu, f); });

    // 05_開発部
    var kaihatsu = dir(root, '05_開発部');
    var projects = dir(kaihatsu, 'プロジェクト');
    var pnames = ['在庫管理システム刷新', '受発注Webポータル', '社内ポータル更改', '生産計画最適化', 'モバイル営業支援アプリ', '基幹DB移行'];
    pnames.forEach(function (pn, pi) {
      var y = years[years.length - 1 - (pi % 3)];
      var pd = dir(projects, 'PRJ-' + y + '-' + ('00' + (11 + pi * 7)).slice(-3) + '_' + pn, at(y, 2, 1));
      [['01_要件定義', ['要件定義書.docx', 'ヒアリングメモ.docx', '業務フロー図.pptx']],
        ['02_設計', ['基本設計書.docx', '詳細設計書.docx', '画面設計書.xlsx', 'DB設計書.xlsx']],
        ['03_開発', ['開発環境構築手順.docx', 'コーディング規約.pdf']],
        ['04_テスト', ['テスト計画書.docx', 'テスト仕様書.xlsx', '不具合管理表.xlsx']],
        ['05_リリース', ['リリース手順書.docx', '移行計画書.xlsx']]].forEach(function (ph) {
        var phd = dir(pd, ph[0], at(y, 3));
        ph[1].forEach(function (f) { file(phd, f, at(y, 3 + Math.floor(rnd() * 9))); });
      });
      var mtg = dir(pd, '定例', at(y, 2));
      for (var i = 1; i <= 8; i++) file(mtg, '進捗定例_第' + i + '回.docx', at(y, 2 + i));
    });
    var ref = dir(kaihatsu, '技術資料');
    ['ＡＷＳ構築メモ.docx', 'Ｇｉｔ運用ルール.pdf', 'ネットワーク構成図.pptx', 'サーバー一覧.xlsx', 'サーバ移行手順.docx'].forEach(function (f) { file(ref, f); });

    // 06_製造部
    var seizo = dir(root, '06_製造部');
    var hinshitsu = dir(seizo, '品質管理');
    years.slice(-3).forEach(function (y) {
      var yd = dir(hinshitsu, y + '年', at(y, 1));
      for (var i = 0; i < 6; i++) {
        var fm = 1 + Math.floor(rnd() * (y === thisYear ? curMonth : 12));
        file(yd, '不良報告書_' + y + mm(fm) + '_' + ('00' + Math.floor(rnd() * 100)).slice(-3) + '.xlsx', at(y, fm));
      }
    });
    var koutei = dir(seizo, '工程表');
    years.slice(-2).forEach(function (y) { for (var m = 1; m <= 12; m++) if (!future(y, m)) file(koutei, '生産工程表_' + y + '-' + mm(m) + '.xlsx', at(y, m)); });
    file(seizo, 'ミツモリ依頼_部材一覧.xlsx', now - 9 * DAY);

    // 99_共有
    var kyoyu = dir(root, '99_共有');
    var tmpl = dir(kyoyu, 'テンプレート');
    ['見積書テンプレート.xlsx', '請求書テンプレート.xlsx', '議事録テンプレート.docx', '稟議書テンプレート.docx', '会社ロゴ.png', 'プレゼン資料テンプレート.pptx'].forEach(function (f) { file(tmpl, f); });
    var manual = dir(kyoyu, 'マニュアル');
    ['複合機の使い方.pdf', 'VPN接続手順.pdf', '勤怠システム操作マニュアル.pdf', 'ファイルサーバー利用ルール.pdf'].forEach(function (f) { file(manual, f); });
    var oki = dir(kyoyu, '一時置き場');
    var old = dir(oki, 'old');
    ['見積書_青葉商事_旧版.xlsx', 'コピー ～ 見積書_北斗工業.xlsx', '新しいフォルダー (2)'].forEach(function (f) {
      if (f.indexOf('.') < 0) dir(old, f); else file(old, f);
    });
    file(oki, 'スキャン_20240611.pdf'); file(oki, '作業中_資料まとめ.xlsx', now - 1 * DAY);

    return b.build();
  }

  // ------------------------------------------------------------------
  // ブラウザで選んだフォルダ (input[webkitdirectory]) からインデックスを作る
  // ------------------------------------------------------------------
  var DEFAULT_EXCLUDE = [/^\$RECYCLE\.BIN$/i, /^System Volume Information$/i, /^~snapshot$/i, /^\.snapshot$/i, /^#recycle$/i,
    /^@Recycle$/i, /^@eaDir$/i, /^Thumbs\.db$/i, /^desktop\.ini$/i, /^\.DS_Store$/i, /^~\$/, /\.tmp$/i];

  function excluded(name) {
    if (name[0] === '.') return true; // 隠しファイル扱い
    for (var i = 0; i < DEFAULT_EXCLUDE.length; i++) if (DEFAULT_EXCLUDE[i].test(name)) return true;
    return false;
  }

  function buildFromFiles(files) {
    var b = new IndexBuilder('/');
    var dirs = new Map();
    var list = Array.prototype.slice.call(files)
      .map(function (f) { return { path: f.webkitRelativePath || f.name, mtime: f.lastModified || 0 }; })
      .sort(function (a, b) { return a.path < b.path ? -1 : a.path > b.path ? 1 : 0; });
    list.forEach(function (f) {
      var parts = f.path.split('/').filter(Boolean);
      if (parts.length === 0) return;
      for (var i = 0; i < parts.length; i++) if (excluded(parts[i]) && i > 0) return;
      var parentNode = -1, acc = '';
      for (var j = 0; j < parts.length - 1; j++) {
        acc += (j ? '/' : '') + parts[j];
        var node = dirs.get(acc);
        if (node === undefined) {
          node = b.add(parentNode, parts[j], 0, true, j === 0);
          dirs.set(acc, node);
        }
        if (b.mtime[node] < f.mtime) b.mtime[node] = f.mtime;
        parentNode = node;
      }
      if (parentNode >= 0) b.add(parentNode, parts[parts.length - 1], f.mtime, false, false);
    });
    return b.build();
  }

  root.FJ = {
    normKey: normKey, parseQuery: parseQuery, Synonyms: Synonyms, DEFAULT_SYNONYMS: DEFAULT_SYNONYMS, eraAlternatives: eraAlternatives,
    KIND_FOLDERS: KIND_FOLDERS, KIND_FILES: KIND_FILES, trimPath: trimPath, lastSegment: lastSegment, parentPath: parentPath,
    isUnder: isUnder, prettyPath: prettyPath, tryParsePath: tryParsePath, highlight: highlight, formatDate: formatDate,
    FileIndex: FileIndex, UsageStore: UsageStore, QueryEngine: QueryEngine, frecency: frecency,
    buildSampleServer: buildSampleServer, buildFromFiles: buildFromFiles, MAX_RESULTS: MAX_RESULTS
  };
  if (typeof module !== 'undefined' && module.exports) module.exports = root.FJ;
})(typeof globalThis !== 'undefined' ? globalThis : this);

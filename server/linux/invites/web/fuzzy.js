// Fuzzy search, the same as SwVault's SOLIDWORKS file browser (src/SwVault.Core/Search/*.cs):
// substrings, initials/abbreviations ("frbrk" -> FrontBracket) and typos ("braket"), multi-term
// queries where every term must match some field, and label:term to search one field.
"use strict";

(function (root) {
  const SUBSTRING_BASE = 100;
  const TYPO_BASE = 45;
  const NONE = -Infinity;

  const isLetterOrDigit = c => /[\p{L}\p{N}]/u.test(c);
  const isLower = c => /\p{Ll}/u.test(c);
  const isUpper = c => /\p{Lu}/u.test(c);
  const isDigit = c => /\p{Nd}/u.test(c);

  // Lower case without changing the length, so positions still point into the original text.
  function lower(text) {
    let out = "";
    for (const c of text) {
      const l = c.toLowerCase();
      out += l.length === c.length ? l : c;
    }
    return out;
  }

  function isWordStart(original, i) {
    if (i === 0) return true;
    const prev = original[i - 1], c = original[i];
    if (!isLetterOrDigit(prev)) return isLetterOrDigit(c);
    if (isLower(prev) && isUpper(c)) return true;
    return isDigit(prev) !== isDigit(c);
  }

  function substring(t, s, original) {
    let bestIndex = -1, bestScore = NONE;
    for (let i = s.indexOf(t); i >= 0; i = s.indexOf(t, i + 1)) {
      let score = SUBSTRING_BASE + 2 * t.length;
      if (isWordStart(original, i)) score += 30;
      if (i === 0) score += 15;
      const end = i + t.length;
      if (end === s.length || isWordStart(original, end) || !isLetterOrDigit(original[end])) score += 10;
      if (score > bestScore) { bestScore = score; bestIndex = i; }
    }
    if (bestIndex < 0) return null;
    bestScore -= Math.min(20, s.length / 8);
    return { score: bestScore, positions: Array.from({ length: t.length }, (_, k) => bestIndex + k) };
  }

  function subsequence(t, s, original) {
    const n = t.length, m = s.length;
    if (n < 2 || n > m || m > 512) return null;
    const score = Array.from({ length: n }, () => new Float64Array(m).fill(NONE));
    const run = Array.from({ length: n }, () => new Int32Array(m));
    const from = Array.from({ length: n }, () => new Int32Array(m));
    for (let j = 0; j < m; j++) {
      if (s[j] !== t[0]) continue;
      score[0][j] = 10 + (isWordStart(original, j) ? 20 : 0) + (j === 0 ? 10 : 0) - Math.min(10, j * 0.5);
      run[0][j] = 1;
      from[0][j] = -1;
    }
    for (let i = 1; i < n; i++) {
      let gapKey = NONE, gapIndex = -1;
      for (let j = i; j < m; j++) {
        if (j >= 2 && score[i - 1][j - 2] !== NONE && score[i - 1][j - 2] + 0.5 * (j - 2) > gapKey) {
          gapKey = score[i - 1][j - 2] + 0.5 * (j - 2);
          gapIndex = j - 2;
        }
        if (s[j] !== t[i]) continue;
        const wordStart = isWordStart(original, j);
        let best = NONE;
        if (score[i - 1][j - 1] !== NONE) {
          best = score[i - 1][j - 1] + 10 + 8 * Math.min(run[i - 1][j - 1], 4) + (wordStart ? 10 : 0);
          from[i][j] = j - 1;
          run[i][j] = run[i - 1][j - 1] + 1;
        }
        if (gapIndex >= 0) {
          const gap = j - gapIndex - 1;
          const viaGap = score[i - 1][gapIndex] + 10 + (wordStart ? 20 : -6) - Math.min(12, gap * 0.5);
          if (viaGap > best) { best = viaGap; from[i][j] = gapIndex; run[i][j] = 1; }
        }
        score[i][j] = best;
      }
    }
    let end = -1, total = NONE;
    for (let j = n - 1; j < m; j++) if (score[n - 1][j] !== NONE && score[n - 1][j] > total) { total = score[n - 1][j]; end = j; }
    if (end < 0) return null;
    const positions = new Array(n);
    for (let i = n - 1, j = end; i >= 0; j = from[i][j], i--) positions[i] = j;
    let anchored = positions.filter(p => isWordStart(original, p)).length;
    for (let k = 1; k < n; k++) if (positions[k] === positions[k - 1] + 1) anchored++;
    if (anchored < Math.floor((n + 1) / 2) + (n >= 4 ? 1 : 0)) return null;
    total = Math.min(total, SUBSTRING_BASE - 5) - Math.min(10, m / 16);
    return { score: total, positions };
  }

  function words(text) {
    const result = [];
    let start = -1;
    for (let i = 0; i <= text.length; i++) {
      const isChar = i < text.length && isLetterOrDigit(text[i]);
      if (start >= 0 && (!isChar || isWordStart(text, i))) { result.push([start, i - start]); start = -1; }
      if (isChar && start < 0) start = i;
    }
    return result;
  }

  function distance(a, b, max) {
    if (Math.abs(a.length - b.length) > max) return max + 1;
    const d = Array.from({ length: a.length + 1 }, (_, i) => { const row = new Int32Array(b.length + 1); row[0] = i; return row; });
    for (let j = 0; j <= b.length; j++) d[0][j] = j;
    for (let i = 1; i <= a.length; i++) {
      let rowMin = Infinity;
      for (let j = 1; j <= b.length; j++) {
        const cost = a[i - 1] === b[j - 1] ? 0 : 1;
        let v = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost);
        if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) v = Math.min(v, d[i - 2][j - 2] + 1);
        d[i][j] = v;
        rowMin = Math.min(rowMin, v);
      }
      if (rowMin > max) return max + 1;
    }
    return d[a.length][b.length];
  }

  function typo(t, s, original) {
    if (t.length < 4) return null;
    const allowed = t.length >= 8 ? 2 : 1;
    let best = null;
    for (const [start, length] of words(original)) {
      if (Math.abs(length - t.length) > allowed && length < t.length) continue;
      for (let len = Math.max(1, t.length - allowed); len <= Math.min(length, t.length + allowed); len++) {
        if (len !== length && len < t.length - allowed) continue;
        const dist = distance(t, s.substr(start, len), allowed);
        if (dist > allowed) continue;
        const score = TYPO_BASE + 2 * t.length - 12 * dist - (len === length ? 0 : 5);
        if (!best || score > best.score) best = { score, positions: Array.from({ length: len }, (_, k) => start + k) };
      }
    }
    return best;
  }

  function match(term, text) {
    if (!term || !text) return null;
    const t = lower(term), s = lower(text);
    let best = substring(t, s, text);
    if (best) return best;
    best = subsequence(t, s, text);
    const tp = typo(t, s, text);
    if (tp && (!best || tp.score > best.score)) best = tp;
    return best;
  }

  function tokenize(query) {
    const tokens = [];
    let current = "", quoted = false;
    for (const c of query) {
      if (c === '"') quoted = !quoted;
      else if (/\s/.test(c) && !quoted) { if (current) tokens.push(current); current = ""; }
      else current += c;
    }
    if (current) tokens.push(current);
    return tokens;
  }

  const NAME_LABEL = "Name";

  // Parses a query once; score(fields) then tests one file. fields: [{label, value, weight}].
  function parseQuery(query) {
    const terms = [];
    for (const raw of tokenize(query || "")) {
      const colon = raw.indexOf(":");
      if (colon > 0 && colon < raw.length - 1) terms.push({ label: raw.slice(0, colon).toLowerCase(), text: raw.slice(colon + 1) });
      else terms.push({ label: null, text: raw.replace(/^:+|:+$/g, "") });
    }
    const real = terms.filter(t => t.text.length > 0);
    return {
      isEmpty: real.length === 0,
      score(fields) {
        if (real.length === 0) return { score: 0, namePositions: [], matchLabel: null, matchValue: null };
        let total = 0, allInName = true, bestOther = null;
        const namePositions = new Set();
        for (const term of real) {
          let best = null;
          for (const field of fields) {
            if (term.label && !field.label.toLowerCase().startsWith(term.label)) continue;
            const m = match(term.text, field.value);
            if (!m) continue;
            const weighted = m.score * field.weight;
            if (!best || weighted > best.score) best = { score: weighted, field, m };
          }
          if (!best) return null;
          total += best.score;
          if (best.field.label === NAME_LABEL) best.m.positions.forEach(p => namePositions.add(p));
          else {
            allInName = false;
            if (!bestOther || best.score > bestOther.score) bestOther = best;
          }
        }
        if (allInName) total += 25;
        return {
          score: total,
          namePositions: [...namePositions].sort((a, b) => a - b),
          matchLabel: bestOther ? bestOther.field.label : null,
          matchValue: bestOther ? bestOther.field.value : null,
        };
      },
    };
  }

  const api = { match, parseQuery, words, distance, NAME_LABEL };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.Fuzzy = api;
})(typeof window !== "undefined" ? window : globalThis);

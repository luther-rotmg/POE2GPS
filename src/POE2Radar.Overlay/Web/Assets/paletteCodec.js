// paletteCodec.js v0.38 Forge theme-code sharing (RUNE1 format)
// Wire format: RUNE1-<base64url(JSON.stringify({n,v}))>-<fnv1a32_hex[0..6]>
// Locked by xUnit PaletteCodecTests.cs - any change here MUST mirror in the C# port.
(function (global) {
  'use strict';
  var MAGIC = 'RUNE1';
  var KEYS = ['gold','goldBright','goldDeep','ink','inkDim','inkFaint','panel','panel2','bg','bgAlt','line','lineSoft','good'];
  var HEX = /^#[0-9a-f]{6}$/;

  function toB64Url(bytes) {
    var s = ''; for (var i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
  function fromB64Url(s) {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    while (s.length % 4) s += '=';
    var bin = atob(s), out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }
  function fnv1a(str) {
    var h = 0x811c9dc5;
    for (var i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    return h >>> 0;
  }
  function checksum(body) {
    var h = fnv1a(body);
    // Use hex encoding to avoid hyphens in wire format
    return h.toString(16).slice(0, 6);
  }

  function encode(palette) {
    if (!palette || typeof palette !== 'object') throw new Error('paletteCodec: palette required');
    var name = String(palette.name == null ? '' : palette.name).trim().slice(0, 40);
    var vars = palette.vars || {};
    var arr = KEYS.map(function (k) { return String(vars[k] == null ? '' : vars[k]).trim().toLowerCase(); });
    for (var i = 0; i < arr.length; i++) if (!HEX.test(arr[i])) throw new Error('paletteCodec: bad color at ' + KEYS[i]);
    var json = JSON.stringify({ n: name, v: arr });
    var body = toB64Url(new TextEncoder().encode(json));
    return MAGIC + '-' + body + '-' + checksum(body);
  }

  function decode(code) {
    if (typeof code !== 'string') return null;
    var parts = code.trim().split('-');
    if (parts.length !== 3 || parts[0] !== MAGIC) return null;
    if (parts[2] !== checksum(parts[1])) return null;
    try {
      var json = new TextDecoder().decode(fromB64Url(parts[1]));
      var p = JSON.parse(json);
      if (!p || !Array.isArray(p.v) || p.v.length !== KEYS.length) return null;
      for (var i = 0; i < p.v.length; i++) if (!HEX.test(String(p.v[i]))) return null;
      var vars = {}; for (var j = 0; j < KEYS.length; j++) vars[KEYS[j]] = p.v[j];
      return { name: String(p.n == null ? '' : p.n).slice(0, 40), vars: vars };
    } catch (e) { return null; }
  }

  global.__paletteCodec = { encode: encode, decode: decode, MAGIC: MAGIC, KEYS: KEYS };
})(window);

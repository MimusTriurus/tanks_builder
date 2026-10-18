// Готовит копии docs/gdd/*.md для документов Fibery (команда /gdd-sync).
//
//   node .claude/scripts/gdd_fibery.mjs status          -> JSON: файл, blob sha, есть ли в карте
//   node .claude/scripts/gdd_fibery.mjs prepare [имена]  -> out/gdd-fibery/<имя>/NN.md + manifest.json
//
// В Fibery пишет не скрипт, а Claude через MCP fibery-studio: у сервера вход
// OAuth, токена для REST нет. Скрипт делает только детерминированную часть:
//  - склеивает строки абзацев и пунктов: в Fibery одиночный перевод строки —
//    жёсткий разрыв, а GDD перенесён по ~80 колонкам;
//  - ссылки на другие файлы GDD ведёт в их документы Fibery, остальные
//    относительные — на GitHub (main);
//  - ставит в начало выноску «это копия»;
//  - режет на куски по заголовкам, чтобы каждый уходил одним вызовом.

import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join, dirname, posix } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const GDD = join(ROOT, "docs", "gdd");
const OUT = join(ROOT, "out", "gdd-fibery");
const MAP = JSON.parse(readFileSync(join(ROOT, ".claude", "gdd-fibery.json"), "utf8"));
// Символов, не байт. Кусок переписывается моделью в вызов инструмента целиком;
// на 12 тыс. субагент обрезал вызов на середине, на 6 — нет.
const CHUNK = 6000;

const gddFiles = () => readdirSync(GDD).filter(f => f.endsWith(".md")).map(f => f.slice(0, -3)).sort();
const blobSha = name => execFileSync("git", ["hash-object", join(GDD, name + ".md")], { cwd: ROOT }).toString().trim();

function rewriteLinks(text) {
  return text.replace(/\]\(([^)\s]+)\)/g, (m, target) => {
    if (/^[a-z]+:/i.test(target) || target.startsWith("#")) return m;
    const [path] = target.split("#");
    const local = path.match(/^([\w-]+)\.md$/);
    if (local && MAP.docs[local[1]]) return `](${MAP.docs[local[1]].url})`;
    const repoPath = posix.normalize(posix.join("docs/gdd", path));
    return `](${MAP.repo}/${repoPath})`;
  });
}

const isMarker = s => /^\s*([-*+]|\d+\.)\s/.test(s);
const isStructural = s => /^#{1,6}\s/.test(s) || /^\s*\|/.test(s) || /^\s*(-{3,}|\*{3,})\s*$/.test(s);

function unwrap(text) {
  const out = [];
  let cur = null, kind = null;
  const flush = () => { if (cur !== null) out.push(cur); cur = null; kind = null; };
  for (const raw of text.replace(/\r\n/g, "\n").split("\n")) {
    const line = raw.replace(/\s+$/, "");
    if (line === "") { flush(); out.push(""); continue; }
    if (/^\s*>/.test(line)) {
      const body = line.replace(/^\s*>\s?/, "");
      if (body.trim() === "") { flush(); out.push(">"); continue; }
      if (kind === "quote" && !isMarker(body) && !isStructural(body)) { cur += " " + body.trim(); continue; }
      flush(); cur = "> " + body; kind = "quote"; continue;
    }
    if (/^\s+\|/.test(line)) {
      // Таблица внутри пункта списка доходит до Fibery с пустыми ячейками —
      // выносим её на верхний уровень, отбивая пустыми строками.
      flush();
      if (out.length && out[out.length - 1] !== "" && !/^\|/.test(out[out.length - 1])) out.push("");
      out.push(line.trimStart());
      continue;
    }
    if (out.length && /^\|/.test(out[out.length - 1]) && !/^\s*\|/.test(line)) { flush(); out.push(""); }
    if (isStructural(line)) { flush(); out.push(line); continue; }
    if (isMarker(line)) { flush(); cur = line; kind = "li"; continue; }
    if (cur !== null) { cur += " " + line.trim(); continue; }
    cur = line; kind = "p";
  }
  flush();
  return out.join("\n").replace(/\n{3,}/g, "\n\n").trim() + "\n";
}

function chunk(text) {
  // Режем перед заголовком, а если секция больше куска — на пустой строке.
  const blocks = text.split(/\n(?=#{1,6} )/);
  const parts = [];
  let cur = "";
  const push = s => { if (cur && cur.length + s.length > CHUNK) { parts.push(cur); cur = ""; } cur += (cur ? "\n" : "") + s; };
  for (const b of blocks) {
    if (b.length <= CHUNK) { push(b); continue; }
    for (const para of b.split(/\n\n/)) push(para + "\n");
  }
  if (cur) parts.push(cur);
  return parts.map(p => p.trim() + "\n");
}

// Отпечаток текста: только буквы и цифры от первого заголовка `# `, FNV-1a.
// Fibery отдаёт markdown по-своему (отступы таблиц, экранирование), а буквы и
// цифры переживают круг без изменений. Та же функция вписана в
// commands/gdd-sync.md, шаг (c) — менять обе вместе.
function fingerprint(md) {
  const i = md.search(/^# /m);
  const s = (i < 0 ? md : md.slice(i)).replace(/[^\p{L}\p{N}]/gu, "");
  let h = 0x811c9dc5;
  for (let k = 0; k < s.length; k++) { h ^= s.charCodeAt(k); h = Math.imul(h, 0x01000193) >>> 0; }
  return { len: s.length, hash: h.toString(16) };
}

function build(name) {
  const src = readFileSync(join(GDD, name + ".md"), "utf8");
  const banner =
    "> [//]: # (callout;icon-type=icon;icon=exclamation-circle;color=#199EE3)\n" +
    `> Копия [docs/gdd/${name}.md](${MAP.repo}/docs/gdd/${name}.md). Правится в git, ` +
    "здесь перезаписывается командой `/gdd-sync` — правки в Fibery пропадут при следующей синхронизации.\n\n";
  return banner + unwrap(rewriteLinks(src));
}

function prepare(name) {
  const body = build(name);
  const parts = chunk(body);
  const dir = join(OUT, name);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  parts.forEach((p, i) => writeFileSync(join(dir, String(i + 1).padStart(2, "0") + ".md"), p));
  const headings = body.split("\n").filter(l => /^#{1,6} /.test(l));
  return { name, sha: blobSha(name), ...MAP.docs[name], chunks: parts.length, dir, headings, fingerprint: fingerprint(body) };
}

const [cmd, ...args] = process.argv.slice(2);
if (cmd === "status") {
  console.log(JSON.stringify(gddFiles().map(n => ({
    name: n, sha: blobSha(n), mapped: !!MAP.docs[n], ...(MAP.docs[n] ? { fingerprint: fingerprint(build(n)) } : {}),
  })), null, 1));
} else if (cmd === "prepare") {
  const names = args.length ? args : gddFiles();
  const missing = names.filter(n => !MAP.docs[n]);
  if (missing.length) { console.error("нет в .claude/gdd-fibery.json: " + missing.join(", ")); process.exit(1); }
  mkdirSync(OUT, { recursive: true });
  const manifest = names.map(prepare);
  writeFileSync(join(OUT, "manifest.json"), JSON.stringify(manifest, null, 1));
  for (const m of manifest) console.log(`${m.name}\t${m.sha}\t${m.chunks} кусков\t${m.headings.length} заголовков\t${m.fingerprint.hash}/${m.fingerprint.len}`);
} else {
  console.error("usage: gdd_fibery.mjs status | prepare [имена]");
  process.exit(2);
}

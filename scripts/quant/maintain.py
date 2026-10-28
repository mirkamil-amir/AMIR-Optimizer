#!/usr/bin/env python3
"""Maintenance checks for MatrixView's formula reference, skills and quantitative code.

Deterministic, offline, standard library only. Commands (exit code 0 = pass, 1 = findings that must be fixed):

  validate         formula reference metadata, code/test/skill references, code coverage, generated index, doc links
  index [--write]  print or write the generated formula index (docs/quant/formula-index.json)
  check-skills     skill frontmatter, paths and links inside skills, skill coverage map, agent instructions
  review-changes   which formulas, skills and tests the current Git changes touch (markdown report)
  audit            status counts, open findings, untested formulas, entries whose code changed after verification
  test-quant       focused quantitative regression suites (.NET unit + ML, Vitest worked examples)
  full-check       validate + check-skills + review-changes + test-quant (+ --integration: ./scripts/test.sh)

Heuristics (changed symbols, usage of formula symbols in a diff) are labelled as such in the report; they point a
reviewer at code, they do not prove anything about its correctness.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "scripts/quant/manifest.json"
SKILLS_DIR = ROOT / ".claude/skills"

STATUSES = {
    "Verified correct",
    "Correct under documented assumptions",
    "Defect confirmed – fixed",
    "Defect confirmed – open",
    "Potential defect requiring more evidence",
    "Undocumented convention",
    "Insufficient data or tests to establish correctness",
    "Not applicable",
    "Not implemented",
}
NEEDS_SEVERITY = {"Defect confirmed – fixed", "Defect confirmed – open", "Potential defect requiring more evidence"}
OPEN_STATUSES = {"Defect confirmed – open", "Potential defect requiring more evidence", "Insufficient data or tests to establish correctness"}
REQUIRED_FIELDS = ["Status", "Code", "Tests", "Skills", "Verified"]
NONE = {"—", "-", "none", ""}


# ------------------------------------------------------------------------------------------------ helpers

def load_manifest() -> dict:
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


def glob_regex(pattern: str) -> re.Pattern[str]:
    """`**` = any number of directories, `*` = anything inside one path segment, `?` = one character."""
    out, i = "", 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out += "(?:.*/)?"
            i += 3
        elif pattern.startswith("**", i):
            out += ".*"
            i += 2
        elif pattern[i] == "*":
            out += "[^/]*"
            i += 1
        elif pattern[i] == "?":
            out += "[^/]"
            i += 1
        else:
            out += re.escape(pattern[i])
            i += 1
    return re.compile(f"^{out}$")


def matches(path: str, patterns: list[str]) -> bool:
    return any(glob_regex(p).match(path) for p in patterns)


def git(*args: str, check: bool = True) -> str:
    r = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout


def tracked_files() -> list[str]:
    files = git("ls-files", "--cached", "--others", "--exclude-standard").splitlines()
    return sorted({f for f in files if (ROOT / f).exists()})


def backticked(cell: str) -> list[str]:
    return re.findall(r"`([^`]+)`", cell)


# ------------------------------------------------------------------------------------------------ formula reference

@dataclass
class Entry:
    id: str
    name: str
    line: int
    fields: dict[str, str] = field(default_factory=dict)

    @property
    def status(self) -> str:
        return self.fields.get("Status", "")

    @property
    def code(self) -> list[tuple[str, str | None]]:
        refs = []
        for token in backticked(self.fields.get("Code", "")):
            path, _, symbol = token.partition("#")
            refs.append((path, symbol or None))
        return refs

    @property
    def tests(self) -> list[str]:
        return backticked(self.fields.get("Tests", ""))

    @property
    def skills(self) -> list[str]:
        value = self.fields.get("Skills", "")
        return [s.strip() for s in re.split(r"[;,]", value) if s.strip() and s.strip() not in NONE]

    @property
    def verified(self) -> dt.date | None:
        m = re.match(r"(\d{4}-\d{2}-\d{2})", self.fields.get("Verified", ""))
        return dt.date.fromisoformat(m.group(1)) if m else None


HEADING = re.compile(r"^### (F-[A-Z]+-\d{3}) · (.+?)\s*$")
ROW = re.compile(r"^\|\s*([^|]+?)\s*\|\s*(.*?)\s*\|\s*$")


def parse_reference(path: Path) -> list[Entry]:
    entries: list[Entry] = []
    current: Entry | None = None
    in_table = False
    for n, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        h = HEADING.match(line)
        if h:
            current = Entry(h.group(1), h.group(2), n)
            entries.append(current)
            in_table = False
            continue
        if line.startswith("## ") or line.startswith("### "):
            current = None
            continue
        if current is None:
            continue
        r = ROW.match(line)
        if r and r.group(1) not in ("Field", "---"):
            if set(r.group(1)) <= {"-", ":"}:
                continue
            current.fields[r.group(1)] = r.group(2)
            in_table = True
        elif in_table and not line.strip():
            in_table = False
    return entries


def build_index(entries: list[Entry]) -> list[dict]:
    return [{
        "id": e.id,
        "name": e.name,
        "status": e.status,
        "severity": e.fields.get("Severity"),
        "code": [f"{p}#{s}" if s else p for p, s in e.code],
        "tests": e.tests,
        "skills": e.skills,
        "verified": e.verified.isoformat() if e.verified else None,
    } for e in entries]


def index_text(entries: list[Entry]) -> str:
    return json.dumps({"$comment": "Generated by scripts/quant/maintain.py index --write from formula-reference.md; do not edit.",
                       "formulas": build_index(entries)}, indent=2, ensure_ascii=False) + "\n"


_test_sources: list[tuple[str, str]] | None = None


def test_sources() -> list[tuple[str, str]]:
    global _test_sources
    if _test_sources is None:
        _test_sources = [(f, (ROOT / f).read_text(encoding="utf-8", errors="replace"))
                         for f in tracked_files() if f.startswith("tests/") and f.endswith(".cs")]
    return _test_sources


def test_exists(ref: str) -> bool:
    if "/" in ref:
        return (ROOT / ref).exists()
    cls, _, method = ref.partition(".")
    for _, text in test_sources():
        if re.search(rf"\bclass {re.escape(cls)}\b", text) and (not method or re.search(rf"\b{re.escape(method)}\s*\(", text)):
            return True
    return False


def symbol_exists(path: str, symbol: str | None) -> bool:
    file = ROOT / path
    if not file.is_file():
        return False
    return symbol is None or re.search(rf"\b{re.escape(symbol)}\b", file.read_text(encoding="utf-8", errors="replace")) is not None


# ------------------------------------------------------------------------------------------------ markdown links

LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")


def slug(heading: str) -> str:
    text = re.sub(r"`([^`]*)`", r"\1", heading.strip().lower())
    text = re.sub(r"[^\w\- ]", "", text)
    return text.replace(" ", "-")


def anchors(md: Path) -> set[str]:
    result, seen = set(), {}
    in_code = False
    for line in md.read_text(encoding="utf-8", errors="replace").splitlines():
        if line.startswith("```"):
            in_code = not in_code
        if in_code:
            continue
        m = re.match(r"^#{1,6}\s+(.*)$", line)
        if m:
            s = slug(m.group(1))
            k = seen.get(s, 0)
            result.add(s if k == 0 else f"{s}-{k}")
            seen[s] = k + 1
    return result


def check_links(files: list[Path]) -> list[str]:
    errors = []
    for md in files:
        text = md.read_text(encoding="utf-8", errors="replace")
        text = re.sub(r"```.*?```", "", text, flags=re.S)
        text = re.sub(r"`[^`\n]*`", "", text)
        for target in LINK.findall(text):
            if re.match(r"^(https?:|mailto:)", target):
                continue
            path_part, _, anchor = target.partition("#")
            dest = md if not path_part else (md.parent / path_part).resolve()
            rel = md.relative_to(ROOT)
            if not dest.exists():
                errors.append(f"{rel}: broken link → {target}")
            elif anchor and dest.suffix == ".md" and anchor not in anchors(dest):
                errors.append(f"{rel}: missing anchor #{anchor} in {dest.relative_to(ROOT)}")
    return errors


def doc_files() -> list[Path]:
    files = [ROOT / "README.md", ROOT / "AGENTS.md", ROOT / "CLAUDE.md"]
    files += sorted((ROOT / "docs").rglob("*.md"))
    files += sorted(SKILLS_DIR.glob("*/SKILL.md"))
    return [f for f in files if f.exists()]


# ------------------------------------------------------------------------------------------------ validate

def cmd_validate(args) -> int:
    manifest = load_manifest()
    ref_path = ROOT / manifest["formulaReference"]
    entries = parse_reference(ref_path)
    errors: list[str] = []
    warnings: list[str] = []
    rel = ref_path.relative_to(ROOT)

    if not entries:
        errors.append(f"{rel}: no formula entries found (headings must look like '### F-ABC-001 · Name').")
    ids = [e.id for e in entries]
    for dup in sorted({i for i in ids if ids.count(i) > 1}):
        errors.append(f"{rel}: formula ID {dup} is used more than once – IDs must be unique and stable.")

    skills = {p.parent.name for p in SKILLS_DIR.glob("*/SKILL.md")}
    referenced_files: set[str] = set()
    for e in entries:
        where = f"{rel}:{e.line} {e.id}"
        for f in REQUIRED_FIELDS:
            if f not in e.fields:
                errors.append(f"{where}: missing field '{f}' in the metadata table.")
        if e.status not in STATUSES:
            errors.append(f"{where}: status '{e.status}' is not one of: {', '.join(sorted(STATUSES))}.")
        if e.status in NEEDS_SEVERITY and not e.fields.get("Severity"):
            errors.append(f"{where}: status '{e.status}' needs a 'Severity' row (High/Medium/Low).")
        code_cell = e.fields.get("Code", "").strip()
        if e.status in ("Not implemented", "Not applicable"):
            if e.code:
                errors.append(f"{where}: marked '{e.status}' but lists code – remove the code or change the status.")
        elif not e.code:
            errors.append(f"{where}: no `path#Symbol` in Code (found '{code_cell}') – an implemented formula must point at its code.")
        for path, symbol in e.code:
            referenced_files.add(path)
            if not (ROOT / path).is_file():
                errors.append(f"{where}: code file {path} does not exist.")
            elif not symbol_exists(path, symbol):
                errors.append(f"{where}: symbol '{symbol}' not found in {path} (renamed? update the entry).")
        tests = e.tests
        if not tests and e.status not in ("Not implemented", "Not applicable"):
            warnings.append(f"{where}: no test validates this formula (coverage gap).")
        for t in tests:
            if not test_exists(t):
                errors.append(f"{where}: test '{t}' not found (expected Class.Method under tests/ or an existing test file path).")
        for s in e.skills:
            if s not in skills:
                errors.append(f"{where}: skill '{s}' has no .claude/skills/{s}/SKILL.md.")
        if e.fields.get("Verified") and e.verified is None:
            errors.append(f"{where}: 'Verified' must start with a date (YYYY-MM-DD).")

    # Every quantitative code file maps to an entry or is declared formula-free.
    files = tracked_files()
    quant = [f for f in files if matches(f, manifest["quantPaths"])]
    exempt = manifest.get("noFormulaFiles", {})
    for f in quant:
        if f not in referenced_files and f not in exempt:
            errors.append(f"{f}: quantitative code file is not referenced by any formula entry – add it to an entry's Code "
                          f"or to noFormulaFiles in {MANIFEST.relative_to(ROOT)} with a reason.")
    for f, reason in exempt.items():
        if not (ROOT / f).exists():
            errors.append(f"{MANIFEST.relative_to(ROOT)}: noFormulaFiles lists {f}, which does not exist.")
        elif f in referenced_files:
            errors.append(f"{MANIFEST.relative_to(ROOT)}: {f} is listed as formula-free but formula entries reference it.")
        elif not reason.strip():
            errors.append(f"{MANIFEST.relative_to(ROOT)}: give a reason why {f} contains no formula.")

    index_path = ROOT / manifest["formulaIndex"]
    expected = index_text(entries)
    if not index_path.exists() or index_path.read_text(encoding="utf-8") != expected:
        errors.append(f"{index_path.relative_to(ROOT)} is out of date – run: python3 scripts/quant/maintain.py index --write")

    errors += check_links(doc_files())

    for w in warnings:
        print(f"warning: {w}")
    for e in errors:
        print(f"error: {e}")
    print(f"validate: {len(entries)} formulas, {len(quant)} quantitative files, {len(errors)} error(s), {len(warnings)} warning(s).")
    return 1 if errors else 0


def cmd_index(args) -> int:
    manifest = load_manifest()
    entries = parse_reference(ROOT / manifest["formulaReference"])
    text = index_text(entries)
    if args.write:
        (ROOT / manifest["formulaIndex"]).write_text(text, encoding="utf-8")
        print(f"wrote {manifest['formulaIndex']} ({len(entries)} formulas)")
    else:
        sys.stdout.write(text)
    return 0


# ------------------------------------------------------------------------------------------------ check-skills

PATH_TOKEN = re.compile(r"^(src|tests|docs|scripts|frontend|tools|deploy|\.claude|\.github)/")


def cmd_check_skills(args) -> int:
    manifest = load_manifest()
    errors: list[str] = []
    skill_files = sorted(SKILLS_DIR.glob("*/SKILL.md"))
    names = set()
    for sf in skill_files:
        rel = sf.relative_to(ROOT)
        text = sf.read_text(encoding="utf-8")
        fm = re.match(r"^---\n(.*?)\n---\n", text, re.S)
        if not fm:
            errors.append(f"{rel}: missing YAML frontmatter (--- name/description ---).")
            continue
        meta = dict(re.findall(r"^(\w+):\s*(.+)$", fm.group(1), re.M))
        name = meta.get("name", "")
        names.add(sf.parent.name)
        if name != sf.parent.name:
            errors.append(f"{rel}: frontmatter name '{name}' must equal the directory name '{sf.parent.name}'.")
        if len(meta.get("description", "")) < 40:
            errors.append(f"{rel}: description is missing or too short to say when the skill applies.")
        body = re.sub(r"```.*?```", lambda m: m.group(0), text, flags=re.S)
        for token in backticked(body):
            t = token.split()[0] if token.split() else token
            t = re.sub(r"(#.*|:\d+.*)$", "", t).rstrip(".,;)")
            if not PATH_TOKEN.match(t) or any(c in t for c in "*<>{}$…") or "..." in t:
                continue
            if not (ROOT / t).exists():
                errors.append(f"{rel}: refers to `{t}`, which does not exist.")

    mapped = set(manifest.get("skills", {}))
    for s in sorted(names - mapped):
        errors.append(f"{MANIFEST.relative_to(ROOT)}: skill '{s}' has no entry in 'skills' (which code does it cover?).")
    for s in sorted(mapped - names):
        errors.append(f"{MANIFEST.relative_to(ROOT)}: 'skills' lists '{s}', but .claude/skills/{s}/SKILL.md does not exist.")
    files = tracked_files()
    for s, globs in manifest.get("skills", {}).items():
        for g in globs:
            if not any(glob_regex(g).match(f) for f in files):
                errors.append(f"{MANIFEST.relative_to(ROOT)}: coverage glob '{g}' of skill '{s}' matches no file.")

    agent_docs = (ROOT / "AGENTS.md").read_text(encoding="utf-8") + (ROOT / "CLAUDE.md").read_text(encoding="utf-8")
    for s in sorted(names):
        if f"/{s}" not in agent_docs and f"`{s}`" not in agent_docs:
            errors.append(f"AGENTS.md/CLAUDE.md: skill '{s}' is not mentioned, so agents are not directed to it.")

    errors += check_links(skill_files)
    for e in errors:
        print(f"error: {e}")
    print(f"check-skills: {len(skill_files)} skills, {len(errors)} error(s).")
    return 1 if errors else 0


# ------------------------------------------------------------------------------------------------ review-changes

DECL = [
    re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|sealed|abstract|partial|readonly|async|override|virtual|required|file)\s+)*(?:class|record|struct|interface|enum)\s+(\w+)"),
    re.compile(r"^\s*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|readonly|unsafe|new)\s+)+[\w<>\[\],.?() ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\("),
    re.compile(r"^\s*(?:public|private|internal|protected)\s+(?:static\s+|readonly\s+|const\s+)*[\w<>\[\],.?]+\s+(\w+)\s*(?:=|;|\{)"),
    re.compile(r"^(?:export\s+)?(?:default\s+)?(?:async\s+)?(?:function|const|let|class|interface|type)\s+(\w+)"),  # top-level TS only
]


def declarations(lines: list[str]) -> list[tuple[int, str]]:
    found = []
    for n, line in enumerate(lines, 1):
        for rx in DECL:
            m = rx.match(line)
            if m and m.group(1) not in ("if", "for", "foreach", "while", "switch", "return", "new", "catch", "using", "lock"):
                found.append((n, m.group(1)))
                break
    return found


def enclosing(decls: list[tuple[int, str]], line: int, text: str = "") -> str | None:
    """Declaration a changed line belongs to; doc comments and attributes belong to the declaration below them."""
    if text.strip().startswith(("///", "/**", "* ", "[")):
        return next((name for n, name in decls if n >= line), None)
    best = None
    for n, name in decls:
        if n <= line:
            best = name
        else:
            break
    return best


def default_base() -> str:
    if os.environ.get("GITHUB_BASE_REF"):
        return f"origin/{os.environ['GITHUB_BASE_REF']}"
    for candidate in ("origin/main", "main"):
        if subprocess.run(["git", "rev-parse", "--verify", "-q", candidate], cwd=ROOT, capture_output=True).returncode == 0:
            mb = git("merge-base", "HEAD", candidate, check=False).strip()
            if mb:
                return mb
    return "HEAD"


@dataclass
class Change:
    path: str
    status: str
    symbols: set[str] = field(default_factory=set)
    added: list[str] = field(default_factory=list)
    removed: list[str] = field(default_factory=list)


def collect_changes(base: str) -> list[Change]:
    changes: dict[str, Change] = {}
    for line in git("diff", "--name-status", "-M", base).splitlines():
        parts = line.split("\t")
        status, path = parts[0][0], parts[-1]
        changes[path] = Change(path, status)
    for path in git("ls-files", "--others", "--exclude-standard").splitlines():
        changes.setdefault(path, Change(path, "A"))

    for ch in changes.values():
        new_lines = (ROOT / ch.path).read_text(encoding="utf-8", errors="replace").splitlines() if (ROOT / ch.path).is_file() else []
        old_text = git("show", f"{base}:{ch.path}", check=False) if ch.status != "A" else ""
        old_lines = old_text.splitlines()
        new_decls, old_decls = declarations(new_lines), declarations(old_lines)
        if ch.status == "A" and not old_text:
            ch.added = new_lines
            ch.symbols = {name for _, name in new_decls}
            continue
        diff = git("diff", "-U0", base, "--", ch.path, check=False)
        old_no = new_no = 0
        for d in diff.splitlines():
            h = re.match(r"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", d)
            if h:
                old_no, new_no = int(h.group(1)), int(h.group(2))
                continue
            if d.startswith("+++") or d.startswith("---"):
                continue
            if d.startswith("+"):
                ch.added.append(d[1:])
                if (s := enclosing(new_decls, new_no, d[1:])):
                    ch.symbols.add(s)
                new_no += 1
            elif d.startswith("-"):
                ch.removed.append(d[1:])
                if (s := enclosing(old_decls, old_no, d[1:])):
                    ch.symbols.add(s)
                old_no += 1
    return sorted(changes.values(), key=lambda c: c.path)


def ack_present(base: str) -> str | None:
    log = git("log", "--format=%B", f"{base}..HEAD", check=False)
    m = re.search(r"^Formula-Review:\s*(.+)$", log, re.M)
    return m.group(1).strip() if m else None


def cmd_review_changes(args) -> int:
    manifest = load_manifest()
    base = args.base or default_base()
    entries = parse_reference(ROOT / manifest["formulaReference"])
    changes = collect_changes(base)
    ref_file = manifest["formulaReference"]
    quant_paths = manifest["quantPaths"]

    def is_quant(p: str) -> bool:
        return matches(p, quant_paths)

    code_changes = [c for c in changes if is_quant(c.path)]
    test_changes = [c for c in changes if c.path.startswith("tests/") or ".test." in c.path or "/e2e/" in c.path]
    doc_changes = [c for c in changes if c.path.endswith(".md")]
    review_hits: dict[str, list[str]] = {}
    for label, globs in manifest.get("reviewPaths", {}).items():
        hit = [c.path for c in changes if matches(c.path, globs)]
        if hit:
            review_hits[label] = hit

    affected: dict[str, tuple[Entry, set[str]]] = {}

    def add(e: Entry, why: str):
        affected.setdefault(e.id, (e, set()))[1].add(why)

    # Heuristics read code only: the knowledge files themselves mention every ID and symbol.
    code_ext = (".cs", ".ts", ".tsx")
    code_files = [c for c in changes if c.path.endswith(code_ext)]
    changed_text = "\n".join(l for c in code_files for l in c.added + c.removed)
    for e in entries:
        for path, symbol in e.code:
            for c in changes:
                if c.path != path:
                    continue
                if symbol and symbol in c.symbols:
                    add(e, f"`{symbol}` changed in {path}")
                elif symbol and any(re.search(rf"\b{re.escape(symbol)}\b", l) for l in c.added + c.removed):
                    add(e, f"lines mentioning `{symbol}` changed in {path}")
                else:
                    add(e, f"same file changed ({path}; other symbols)")
            if symbol:
                # A qualified call (File.Symbol) or a distinctive name; generic names (Build, Compute) only when qualified.
                usage = rf"\b{re.escape(Path(path).stem)}\.{re.escape(symbol)}\b" + (rf"|\b{re.escape(symbol)}\b" if len(symbol) >= 12 else "")
                users = [c.path for c in code_files if c.path != path and any(re.search(usage, l) for l in c.added + c.removed)]
                for u in users:
                    add(e, f"usage of `{symbol}` changed in {u} (heuristic)")
        if re.search(rf"\b{re.escape(e.id)}\b", changed_text):
            add(e, "formula ID mentioned in changed lines")

    skills_hit: dict[str, list[str]] = {}
    for skill, globs in manifest.get("skills", {}).items():
        hit = [c.path for c in changes if matches(c.path, globs)]
        if hit:
            skills_hit[skill] = hit
    for e, _ in affected.values():
        for s in e.skills:
            skills_hit.setdefault(s, [])

    unmapped = [c.path for c in code_changes if not any(c.path == p for e in entries for p, _ in e.code)
                and c.path not in manifest.get("noFormulaFiles", {})]
    untested = [e for e, _ in affected.values() if not e.tests]
    math_review = bool(code_changes) or bool(review_hits)
    ref_updated = any(c.path == ref_file for c in changes)
    skills_updated = sorted({c.path for c in changes if c.path.startswith(".claude/skills/")})
    ack = args.ack or ack_present(base)

    out: list[str] = []
    w = out.append
    w("# Quantitative change review")
    w("")
    w(f"Base: `{base}` · compared with the working tree (staged, unstaged and untracked files). Generated by "
      "`scripts/quant/maintain.py review-changes`; symbol detection is a heuristic.")
    w("")
    w(f"* Mathematical review required: **{'yes' if math_review else 'no'}**"
      + (f" – {len(code_changes)} quantitative file(s)" if code_changes else "")
      + (f", contract/schema/config/explanation files: {', '.join(review_hits)}" if review_hits else ""))
    w(f"* Formula reference updated: **{'yes' if ref_updated else 'no'}**" + (f" · acknowledgement: {ack}" if ack else ""))
    w(f"* Skills updated: {', '.join(f'`{s}`' for s in skills_updated) if skills_updated else 'none'}")
    w(f"* Tests changed: {len(test_changes)} file(s) · docs changed: {len(doc_changes)} file(s)")
    w("")
    w("## Changed files and symbols")
    w("")
    if not changes:
        w("No changes.")
    for c in changes:
        kind = "quant" if is_quant(c.path) else "test" if c in test_changes else "doc" if c in doc_changes else "other"
        syms = ", ".join(f"`{s}`" for s in sorted(c.symbols)[:12]) + (" …" if len(c.symbols) > 12 else "")
        w(f"* {c.status} `{c.path}` ({kind}){': ' + syms if syms else ''}")
    w("")
    w("## Potentially affected formulas")
    w("")
    if not affected:
        w("None.")
    for fid in sorted(affected):
        e, why = affected[fid]
        w(f"* **{fid} · {e.name}** ({e.status}) – {'; '.join(sorted(why))}")
        w(f"  * tests: {', '.join(f'`{t}`' for t in e.tests) if e.tests else '**none**'} · skills: {', '.join(e.skills) or 'none'}")
    w("")
    w("## Skills that may need an update")
    w("")
    if not skills_hit:
        w("None.")
    for s in sorted(skills_hit):
        files = skills_hit[s]
        w(f"* `{s}`{' – ' + ', '.join(f'`{f}`' for f in files[:6]) if files else ' (via affected formulas)'}"
          + (" – updated" if f".claude/skills/{s}/SKILL.md" in skills_updated else ""))
    w("")
    w("## Coverage gaps and risks")
    w("")
    gaps = False
    for p in unmapped:
        gaps = True
        w(f"* `{p}` is quantitative code without a formula entry – add one (or list it as formula-free).")
    for e in untested:
        gaps = True
        w(f"* {e.id} has no test – add a hand-computed or independent-reference test.")
    if math_review and not ref_updated and not ack:
        gaps = True
        w("* Quantitative code changed but the formula reference did not: update the affected entries (Status, Verified, "
          "Tests) or record why none is needed with a `Formula-Review: <reason>` commit trailer.")
    for label, files in review_hits.items():
        gaps = True
        w(f"* {label}: {', '.join(f'`{f}`' for f in files[:6])} – check units, meaning and the docs that describe them.")
    if not gaps:
        w("None found.")
    w("")
    w("## Before merging")
    w("")
    w("1. `python3 scripts/quant/maintain.py validate` and `check-skills` pass.")
    w("2. `python3 scripts/quant/maintain.py test-quant` passes; behaviour changes have a test that fails on the old code.")
    w("3. A person has reviewed each affected formula against its definition (the tool cannot judge correctness).")

    report = "\n".join(out) + "\n"
    if args.output:
        Path(args.output).write_text(report, encoding="utf-8")
        print(f"review written to {args.output}")
    else:
        sys.stdout.write(report)
    if args.strict and math_review and code_changes and not ref_updated and not ack:
        print("error: quantitative code changed without a formula-reference update or a 'Formula-Review:' acknowledgement.", file=sys.stderr)
        return 1
    return 0


# ------------------------------------------------------------------------------------------------ audit

def cmd_audit(args) -> int:
    manifest = load_manifest()
    entries = parse_reference(ROOT / manifest["formulaReference"])
    counts: dict[str, int] = {}
    for e in entries:
        counts[e.status] = counts.get(e.status, 0) + 1
    print(f"Formula reference: {len(entries)} entries")
    for s in sorted(counts, key=lambda x: -counts[x]):
        print(f"  {counts[s]:3d}  {s}")
    print("\nOpen findings (defects, potential defects, insufficient evidence):")
    found = [e for e in entries if e.status in OPEN_STATUSES]
    for e in found:
        print(f"  {e.id} [{e.fields.get('Severity', '?')}] {e.name} – {e.status}")
    if not found:
        print("  none")
    print("\nFormulas without tests:")
    untested = [e for e in entries if not e.tests and e.status not in ("Not implemented", "Not applicable")]
    for e in untested:
        print(f"  {e.id} {e.name}")
    if not untested:
        print("  none")

    print("\nVerification older than the code (commits after the Verified date touching the entry's files):")
    stale = 0
    for e in entries:
        if not e.verified:
            continue
        files = sorted({p for p, _ in e.code if (ROOT / p).exists()})
        if not files:
            continue
        since = (e.verified + dt.timedelta(days=1)).isoformat()
        commits = git("log", "--format=%h %ad %s", "--date=short", f"--since={since}", "--", *files, check=False).splitlines()
        if commits:
            stale += 1
            print(f"  {e.id} verified {e.verified} – {len(commits)} later commit(s), e.g. {commits[0]}")
    if stale == 0:
        print("  none")
    age_limit = args.max_age_days
    old = [e for e in entries if e.verified and (dt.date.today() - e.verified).days > age_limit]
    print(f"\nEntries not re-verified for more than {age_limit} days: {len(old)}")
    for e in old:
        print(f"  {e.id} verified {e.verified}")
    if args.fail_on_stale and (stale or old):
        return 1
    return 0


# ------------------------------------------------------------------------------------------------ tests

def run(cmd: list[str], cwd: Path = ROOT) -> int:
    print(f"$ {' '.join(cmd)}", flush=True)
    return subprocess.run(cmd, cwd=cwd).returncode


def cmd_test_quant(args) -> int:
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
    if not Path(dotnet).exists() and not shutil.which("dotnet"):
        print("error: dotnet not found – install the .NET 10 SDK or export PATH=\"$HOME/.dotnet:$PATH\".")
        return 1
    status = 0
    for project in ("tests/PortfolioOptimizer.UnitTests", "tests/PortfolioOptimizer.MachineLearning.Tests"):
        status |= run([dotnet, "test", project])
    if not args.no_frontend:
        fe = ROOT / "frontend/portfolio-dashboard"
        vitest = fe / "node_modules/.bin/vitest"
        if vitest.exists():
            status |= run([str(vitest), "run", "src/lib/worked-examples.test.ts", "src/lib/interpretation.test.ts"], cwd=fe)
        else:
            print("skipped: frontend worked-example tests (node_modules missing – run npm ci in frontend/portfolio-dashboard).")
    return 1 if status else 0


def cmd_full_check(args) -> int:
    results = {}
    results["validate"] = cmd_validate(args)
    results["check-skills"] = cmd_check_skills(args)
    args.output = args.output or None
    results["review-changes"] = cmd_review_changes(args)
    results["test-quant"] = cmd_test_quant(args)
    if args.integration:
        results["integration (scripts/test.sh)"] = run(["./scripts/test.sh"])
    print("\nfull-check summary:")
    for k, v in results.items():
        print(f"  {'PASS' if v == 0 else 'FAIL'}  {k}")
    return 1 if any(results.values()) else 0


# ------------------------------------------------------------------------------------------------ main

def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("validate")
    ip = sub.add_parser("index")
    ip.add_argument("--write", action="store_true")
    sub.add_parser("check-skills")
    for name in ("review-changes", "full-check"):
        rp = sub.add_parser(name)
        rp.add_argument("--base", help="Git ref to compare with (default: merge-base with origin/main or main)")
        rp.add_argument("--output", help="write the markdown report to this file")
        rp.add_argument("--strict", action="store_true", help="fail when quant code changed without a formula-reference update or acknowledgement")
        rp.add_argument("--ack", help="acknowledge that no formula-reference change is needed (reason)")
        if name == "full-check":
            rp.add_argument("--no-frontend", action="store_true")
            rp.add_argument("--integration", action="store_true", help="also run ./scripts/test.sh (needs PostgreSQL via Podman/Docker)")
    ap = sub.add_parser("audit")
    ap.add_argument("--max-age-days", type=int, default=180)
    ap.add_argument("--fail-on-stale", action="store_true")
    tp = sub.add_parser("test-quant")
    tp.add_argument("--no-frontend", action="store_true")
    args = p.parse_args(argv)
    handler = {
        "validate": cmd_validate, "index": cmd_index, "check-skills": cmd_check_skills, "review-changes": cmd_review_changes,
        "audit": cmd_audit, "test-quant": cmd_test_quant, "full-check": cmd_full_check,
    }[args.command]
    return handler(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

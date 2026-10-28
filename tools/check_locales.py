# -*- coding: utf-8 -*-
"""Сверка локалей с кодом (Loc.Tr/Loc.F/Loc.Plural + словари состояний + профили).

Использование:
  python tools/check_locales.py            — проверка (ненулевой код при провале)
  python tools/check_locales.py --fill-en  — добить en.json значениями словарей
                                             состояний (en = английский оригинал-ключ)
"""
import io
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "SupremeNuclear")
LOCALES = os.path.join(ROOT, "locales")

LIT = r'"((?:[^"\\]|\\.)*)"'
CYR = re.compile("[А-Яа-яЁё]")


def collect_keys():
    keys = set()
    for base, _, files in os.walk(SRC):
        for f in files:
            if not f.endswith(".cs"):
                continue
            s = io.open(os.path.join(base, f), encoding="utf-8", errors="replace").read()
            for m in re.finditer(r"Loc\.Tr\(" + LIT + r"\)", s):
                if CYR.search(m.group(1)):
                    keys.add(m.group(1))
            for m in re.finditer(r"Loc\.F\(" + LIT, s):
                if CYR.search(m.group(1)):
                    keys.add(m.group(1))
            for m in re.finditer(r"Loc\.Plural\([^,]+,\s*" + LIT + r",\s*" + LIT + r",\s*" + LIT + r"\)", s):
                joined = m.group(1) + "|" + m.group(2) + "|" + m.group(3)
                if CYR.search(joined):
                    keys.add(joined)
    # имена профилей высоты локализуются на чтении (Loc.Tr(p.Name) в контроллере)
    air = io.open(os.path.join(SRC, "Aviation", "AircraftOrders.cs"), encoding="utf-8").read()
    for m in re.finditer(r"FlightProfile\([^)]*\"([^\"]*)\"\)", air):
        if CYR.search(m.group(1)):
            keys.add(m.group(1))
    return keys


def state_dictionary_pairs():
    """Пары (английский оригинал, русский ключ) из словарей состояний Catalog:
    значения идут через Loc.Tr, английский оригинал — готовый перевод en."""
    cat = io.open(os.path.join(SRC, "UiRts", "Catalog.cs"), encoding="utf-8").read()
    lit = r'"((?:[^"\\]|\\.)*)"'
    return [(m.group(1), m.group(2)) for m in re.finditer(r"\{ " + lit + r", " + lit + r" \},", cat)]


def main():
    fill = "--fill-en" in sys.argv
    keys = collect_keys()
    pairs = state_dictionary_pairs()
    for _, ru in pairs:
        keys.add(ru)
    failures = []
    for name in sorted(os.listdir(LOCALES)):
        if not name.endswith(".json"):
            continue
        path = os.path.join(LOCALES, name)
        table = json.load(io.open(path, encoding="utf-8"))
        locale_keys = {k for k in table if not k.startswith("_")}
        missing = sorted(k for k in keys if k not in locale_keys)
        unused = sorted(k for k in locale_keys if k not in keys)
        # словарные состояния: en-значение обязано совпадать с оригиналом-ключом
        if name == "en.json":
            for en, ru in pairs:
                if table.get(ru) not in (None, en):
                    failures.append("%s: состояние %r переведено как %r (ожидался оригинал %r)"
                                    % (name, ru, table[ru], en))
        if missing:
            failures.append("%s: нет переводов (%d): %s" % (name, len(missing), " | ".join(missing[:12])))
        if unused:
            failures.append("%s: неиспользуемые ключи (%d): %s" % (name, len(unused), " | ".join(unused[:12])))
        print(("[OK]   " if not (missing or unused) else "[FAIL] ") + name +
              " — ключей в коде %d, в локали %d" % (len(keys), len(locale_keys)))
    if fill:
        path = os.path.join(LOCALES, "en.json")
        table = json.load(io.open(path, encoding="utf-8"))
        added = 0
        for en, ru in pairs:
            if ru not in table:
                table[ru] = en
                added += 1
        ordered = {"_plural": table.get("_plural", "en")}
        for k in sorted(k for k in table if not k.startswith("_")):
            ordered[k] = table[k]
        io.open(path, "w", encoding="utf-8", newline="\n").write(
            json.dumps(ordered, ensure_ascii=False, indent=2) + "\n")
        print("en.json: добавлено словарных состояний: %d" % added)
    if failures:
        print()
        for f in failures:
            print("[FAIL] " + f)
        sys.exit(1)


if __name__ == "__main__":
    main()

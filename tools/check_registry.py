# -*- coding: utf-8 -*-
"""Проверка реестра долгов ARCHITECTURE §12: номер строки уникален.

Первый столбец таблицы §12 — номер долга; на него ссылаются код и доки
(«§12-NN»), поэтому дубликат целого номера делает ссылки неоднозначными.
Правило: новый долг = max+1; литерные варианты («66-в») запрещены — записи,
ошибочно занявшие чужой номер, переносятся на свежий номер с пометкой
«бывш. №NN» в колонке «Долг».

Использование:  python tools/check_registry.py   (ненулевой код при провале)
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCH = os.path.join(ROOT, "ARCHITECTURE.md")


def section_rows():
    """Строки таблицы §12: (номер, превью) либо (None, превью) для брака."""
    lines = io.open(ARCH, encoding="utf-8").read().split("\n")
    start = end = None
    for i, line in enumerate(lines):
        if line.startswith("## 12."):
            start = i + 1
        elif start is not None and line.startswith("## "):
            end = i
            break
    if start is None:
        return None
    rows = []
    for line in lines[start:end]:
        m = re.match(r"^\|\s*(\d+)\s*\|", line)
        if m:
            rows.append((int(m.group(1)), line[:80]))
        elif re.match(r"^\|\s*\d+\D", line):
            # начинается с цифры, но первый столбец не целый номер («66-в» и т.п.)
            rows.append((None, line[:80]))
    return rows


def main():
    rows = section_rows()
    if not rows:
        print("Реестр: секция §12 не найдена или пуста в ARCHITECTURE.md")
        sys.exit(1)
    failures = []
    seen = {}
    for num, preview in rows:
        if num is None:
            failures.append("литерный номер (заведи новый целый, max+1): %s" % preview)
            continue
        if num in seen:
            failures.append("дубликат №%d: %s && %s" % (num, seen[num][:50], preview[:50]))
        else:
            seen[num] = preview
    if failures:
        for f in failures:
            print("[FAIL] " + f)
        sys.exit(1)
    print("[OK]   Реестр долгов §12: %d записей, номера уникальны, max = %d"
          % (len(rows), max(seen)))


if __name__ == "__main__":
    main()

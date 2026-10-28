# -*- coding: utf-8 -*-
"""Сборочный конвейер Supreme Nuclear: clean-сборка (предупреждения = ошибки),
версия от исходника до задеплоенного DLL, линт накопленных классов багов,
тесты чистой логики. Выход ≠ 0 при любой проблеме.

Запуск:  python tools/build.py          (полный конвейер)
         python tools/build.py --fast   (без чистки obj)
"""
import io
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "SupremeNuclear")
TESTS = os.path.join(ROOT, "tests")
PLUGIN_INFO = os.path.join(SRC, "PluginInfo.cs")
BIN_DLL = os.path.join(SRC, "bin", "Release", "SupremeNuclear.dll")
GAME_DLL = r"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\SupremeNuclear\SupremeNuclear.dll"
GAME_LOCALES = r"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\SupremeNuclear\Locales"
REPO_LOCALES = os.path.join(ROOT, "locales")

failures = []


def step(name, ok, details=""):
    print(("[OK]   " if ok else "[FAIL] ") + name + ((" — " + details) if details else ""))
    if not ok:
        failures.append(name)


def run(cmd, cwd=None):
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, shell=True)
    text = (p.stdout or b"") + (p.stderr or b"")
    return p.returncode, text.decode("cp1251", errors="replace")


def read(path):
    with io.open(path, encoding="utf-8") as f:
        return f.read()


def source_files():
    for base, _, files in os.walk(SRC):
        for f in files:
            if f.endswith(".cs"):
                yield os.path.join(base, f)


def main():
    fast = "--fast" in sys.argv

    # ---------- 1. Версия ----------
    m = re.search(r'Version\s*=\s*"(\d+\.\d+\.\d+)"', read(PLUGIN_INFO))
    version = m.group(1) if m else None
    step("Версия в PluginInfo.cs распознана", version is not None, version or "нет")
    if version == "0.35.2":
        step("Версия не застряла на 0.35.2", False, "инцидент повторился")
    else:
        step("Версия не застряла на 0.35.2", True)

    # ---------- 2. Линт ----------
    double_text = 0
    go_without_rt = 0
    for path in source_files():
        lines = read(path).split("\n")
        for i, line in enumerate(lines):
            if "new GameObject(" in line:
                if "typeof(Text)" in line:
                    window = "\n".join(lines[i:i + 5])
                    if "AddComponent<Text>()" in window:
                        double_text += 1
                if ("typeof(RectTransform)" not in line and
                        "typeof(Canvas)" not in line and "typeof(EventSystem)" not in line and
                        "// 3d-go" not in line.lower()):
                    go_without_rt += 1
                    print("      без RectTransform: %s:%d" % (os.path.relpath(path, ROOT), i + 1))
    step("Линт: нет двойного Text (typeof(Text)+AddComponent)", double_text == 0,
         "нарушений: %d" % double_text)
    step("Линт: GameObject- ctor только с typeof(RectTransform)", go_without_rt == 0,
         "нарушений: %d" % go_without_rt)

    # ---------- 2b. Локали: ключи кода = ключи каждой локали ----------
    code, out = run('python "%s"' % os.path.join(ROOT, "tools", "check_locales.py"))
    step("Локали: ключи Loc.* покрыты всеми файлами locales/", code == 0,
         out.strip().split("\n")[-1] if out.strip() else "")

    # ---------- 3. Сборка (полная, предупреждения = ошибки) ----------
    if not fast:
        subprocess.run("rm -rf obj", cwd=SRC, shell=True, capture_output=True)
    code, out = run("dotnet build -c Release -warnaserror", cwd=SRC)
    step("Сборка 0 ошибок / 0 предупреждений", code == 0, out.strip().split("\n")[-1] if out else "")

    # ---------- 4. Версия в бинарнике и деплой ----------
    if os.path.exists(BIN_DLL):
        data = open(BIN_DLL, "rb").read()
        step("Версия в bin DLL совпадает с исходником",
            version.encode() in data, version or "")
    else:
        step("bin DLL существует", False)
    if os.path.exists(GAME_DLL):
        gdata = open(GAME_DLL, "rb").read()
        step("Деплой: версия в игровом DLL совпадает", version.encode() in gdata)
        step("Деплой: bin и игровой DLL идентичны",
            open(BIN_DLL, "rb").read() == gdata if os.path.exists(BIN_DLL) else False)
        locales_ok = os.path.isdir(GAME_LOCALES)
        if locales_ok:
            for name in os.listdir(REPO_LOCALES):
                if name.endswith(".json"):
                    game_json = os.path.join(GAME_LOCALES, name)
                    repo_json = os.path.join(REPO_LOCALES, name)
                    if not os.path.exists(game_json) or open(game_json, "rb").read() != open(repo_json, "rb").read():
                        locales_ok = False
                        break
        step("Деплой: локали в игре идентичны репозиторию", locales_ok)
    else:
        step("Игровой DLL существует", False, GAME_DLL)

    # ---------- 5. Тесты чистой логики ----------
    code, out = run("dotnet build -c Release", cwd=TESTS)
    if code == 0:
        exe = os.path.join(TESTS, "bin", "Release", "SupremeNuclear.Tests.exe")
        p = subprocess.run([exe], capture_output=True)
        print(p.stdout.rstrip())
        step("Тесты чистой логики", p.returncode == 0)
    else:
        step("Сборка тестов", False, out[-400:])

    print()
    if failures:
        print("КОНВЕЙЕР ПРОВАЛЕН: " + ", ".join(failures))
        sys.exit(1)
    print("КОНВЕЙЕР ЗЕЛЁНЫЙ (v%s)" % version)


if __name__ == "__main__":
    main()

"""Автопубликация Supreme Nuclear на GitHub: пуш master → релиз со свежим
zip. Одна команда после коммита = игроки качают актуальный билд.

Запуск:  python tools/publish.py                # пуш master + релиз v<версия>
         python tools/publish.py --no-push      # упаковка без пуша/релиза (проверка)
         python tools/publish.py --no-release   # пуш master без релиза (правки доков)

Почему так: GitHub Actions не соберёт мод (reference-сборки берутся из
установленной игры), поэтому релиз делает эта машина. Артефакт собирается
в ОТДЕЛЬНОМ git-worktree от ЗАКОММИЧЕННОГО master — общий bin делится с
параллельными сессиями, и однажды zip уже получал чужой WIP-бинарь
(2026-10-28).

Одна ветка master (2026-11-04, юзер-выбор): прежде публиковались орфан-
снимки в main — теперь декомпил/reference вырезаны из истории (filter-repo)
и закрыты .gitignore, снимки потеряли смысл, а баннер Compare&PR и
несравнимые ветки — ушли. Релиз-тег вешается на коммит master.

Требует: git, gh (gh auth login — один раз), установленную игру (GameDir).
"""
import io
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "SupremeNuclear")
TESTS = os.path.join(ROOT, "tests")
PLUGIN_INFO = os.path.join(SRC, "PluginInfo.cs")
WORKTREE = os.path.join(os.path.dirname(ROOT), "supreme_nuclear-publish")
DIST = os.path.join(ROOT, "dist")

# Что НИКОГДА не попадает в публикуемую ветку
EXCLUDED = ["decompiled", "GroundControlRts", ".zcode"]

failures = []


def step(name, ok, detail=""):
    print("[%s]   %s%s" % ("OK" if ok else "FAIL", name, (" — " + str(detail)) if detail else ""))
    if not ok:
        failures.append(name)


def run(cmd, cwd=None):
    p = subprocess.run(cmd, cwd=cwd, capture_output=True)
    text = (p.stdout or b"") + (p.stderr or b"")
    return p.returncode, text.decode("utf-8", errors="replace")


def main():
    no_push = "--no-push" in sys.argv
    no_release = "--no-release" in sys.argv
    will_release = not no_push and not no_release
    # Версия — из ЗАКОММИЧЕННОГО master: в рабочем дереве может лежать WIP
    # параллельной сессии с другой версией (инцидент 2026-10-28).
    code, src = run(["git", "-C", ROOT, "show", "master:src/SupremeNuclear/PluginInfo.cs"])
    m = re.search(r'Version\s*=\s*"(\d+\.\d+\.\d+)"', src) if code == 0 else None
    version = m.group(1) if m else None
    step("Версия master распознана", version is not None, version or "нет")
    if failures:
        sys.exit(1)

    if not no_push:
        code, out = run(["gh", "auth", "status"])
        step("gh авторизован", code == 0, "нет — gh auth login")
        code, out = run(["git", "remote", "get-url", "origin"])
        step("remote origin задан", code == 0,
             "нет — единожды: gh repo create supreme_nuclear --public --source . --remote origin (БЕЗ --push)")
    if will_release:
        code, out = run(["gh", "release", "view", "v" + version])
        step("Релиз v%s ещё не существует" % version, code != 0,
             "уже есть — бампни версию в PluginInfo.cs")
    if failures:
        sys.exit(1)

    # ---------- 1. Гигиена master: запрещённые пути не в индексе ----------
    code, master_sha = run(["git", "-C", ROOT, "rev-parse", "master"])
    master_sha = master_sha.strip()
    step("master найден", code == 0, master_sha[:9])
    p = subprocess.run(["git", "-C", ROOT, "ls-files", "--"] +
                       [path + "/" for path in EXCLUDED], capture_output=True)
    leftover = p.stdout.decode().strip()
    step("Запрещённые пути вне индекса master", leftover == "",
         leftover.splitlines()[0] if leftover else ", ".join(EXCLUDED) + " — gitignore")

    # ---------- 2. Сборка артефакта в изолированном worktree ----------
    run(["git", "-C", ROOT, "worktree", "remove", "--force", WORKTREE])
    if os.path.exists(WORKTREE):
        shutil.rmtree(WORKTREE, ignore_errors=True)
    code, out = run(["git", "-C", ROOT, "worktree", "add", "--detach", WORKTREE, "master"])
    step("Worktree создан", code == 0, WORKTREE)

    if not failures:
        try:
            code, out = run(["dotnet", "build", os.path.join("src", "SupremeNuclear",
                        "SupremeNuclear.csproj"), "-c", "Release", "-p:Deploy=false"], cwd=WORKTREE)
            step("Сборка master 0 ошибок", code == 0 and ("Ошибок: 0" in out or "0 Error" in out),
                 out[-300:] if code else "")
            dll = os.path.join(WORKTREE, "src", "SupremeNuclear", "bin", "Release",
                               "SupremeNuclear.dll")
            data = open(dll, "rb").read() if os.path.exists(dll) else b""
            step("Байтовая проба версии в DLL",
                 version.encode() in data if version else False, version)
            step("Сборка тестов", run(["dotnet", "build", "-c", "Release"],
                 cwd=os.path.join(WORKTREE, "tests"))[0] == 0)
            exe = os.path.join(WORKTREE, "tests", "bin", "Release", "SupremeNuclear.Tests.exe")
            p = subprocess.run([exe], capture_output=True)
            step("Тесты чистой логики", p.returncode == 0)
            code, out = run(["python", os.path.join("tools", "check_locales.py")], cwd=WORKTREE)
            step("Локали", code == 0)

            # ---------- 3. Zip ----------
            if os.path.exists(DIST):
                shutil.rmtree(DIST)
            stage = os.path.join(DIST, "SupremeNuclear")
            os.makedirs(os.path.join(stage, "Locales"))
            shutil.copy2(dll, stage)
            for name in os.listdir(os.path.join(WORKTREE, "locales")):
                if name.endswith(".json"):
                    shutil.copy2(os.path.join(WORKTREE, "locales", name),
                                 os.path.join(stage, "Locales", name))
            # Сплат-шейдер рельефа (v0.144): без бандла — фолбэк на гибрид;
            # бандл лежит в репо (splatshader/splat.bundle, пересборка — CLI
            # Unity 2022.3.62, см. splatshader/README). MapGen-пресеты: 4 стиля
            # сразу (без них у игрока один образец после первого Ctrl+G).
            bundle = os.path.join(WORKTREE, "splatshader", "splat.bundle")
            if os.path.exists(bundle):
                shutil.copy2(bundle, os.path.join(stage, "splat.bundle"))
            presets = os.path.join(WORKTREE, "mapgen")
            if os.path.isdir(presets):
                os.makedirs(os.path.join(stage, "MapGen"), exist_ok=True)
                for name in os.listdir(presets):
                    if name.endswith(".json"):
                        shutil.copy2(os.path.join(presets, name),
                                     os.path.join(stage, "MapGen", name))
            zip_path = os.path.join(DIST, "SupremeNuclear-v%s.zip" % version)
            with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
                for base, _, files in os.walk(stage):
                    for f in files:
                        full = os.path.join(base, f)
                        z.write(full, os.path.relpath(full, DIST))
            step("Zip собран", os.path.exists(zip_path),
                 "%d КБ" % (os.path.getsize(zip_path) // 1024))
        finally:
            run(["git", "-C", ROOT, "worktree", "remove", "--force", WORKTREE])

    if failures:
        print("\nПУБЛИКАЦИЯ ПРОВАЛЕНА: " + ", ".join(failures))
        sys.exit(1)

    # ---------- 4. Пуш и релиз ----------
    if no_push:
        print("\nСУХОЙ ПРОГОН ЗЕЛЁНЫЙ (v%s) — пуш/релиз пропущены (--no-push)" % version)
        return
    code, subject = run(["git", "-C", ROOT, "log", "-1", "--format=%s", "master"])
    if code != 0:
        subject = "Supreme Nuclear v%s" % version
    code, out = run(["git", "-C", ROOT, "push", "origin", "master"])
    step("Пуш master", code == 0, out[-200:] if code else "")
    if will_release:
        notes = "Автопубликация из master: %s" % subject.strip()
        code, out = run(["gh", "release", "create", "v" + version, "--target", "master",
                         zip_path, "--title", "v" + version, "--notes", notes], cwd=ROOT)
        step("Релиз создан", code == 0, out[-200:] if code else "releases/tag/v" + version)
    if failures:
        print("\nПУШ/РЕЛИЗ ПРОВАЛЕН: " + ", ".join(failures))
        sys.exit(1)
    print("\nОПУБЛИКОВАНО: master запушен%s" % (
        ", релиз v%s — игроки качают из releases/latest" % version if will_release else " (без релиза)"))


if __name__ == "__main__":
    main()

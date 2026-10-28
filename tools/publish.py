"""Автопубликация Supreme Nuclear на GitHub: снимок master → ветка main → пуш →
релиз со свежим zip. Одна команда после коммита = игроки качают актуальный билд.

Запуск:  python tools/publish.py            # синк main + пуш + релиз v<версия>
         python tools/publish.py --no-push  # упаковка без пуша/релиза (проверка)

Почему так: GitHub Actions не соберёт мод (reference-сборки берутся из
установленной игры), поэтому релиз делает эта машина. Артефакт собирается
в ОТДЕЛЬНОМ git-worktree от main — общий bin делится с параллельными
сессиями, и однажды zip уже получал чужой WIP-бинарь (2026-10-28).

Ветка main — только чистые снимки master: decompiled/, GroundControlRts/
и .zcode/ в неё не попадают никогда (в истории master их пушить нельзя).
Конвейер на общем дереве (tools/build.py) publish не запускает — он гоняет
те же проверки там, где реально собирается артефакт: в worktree.

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
        code, out = run(["gh", "release", "view", "v" + version])
        step("Релиз v%s ещё не существует" % version, code != 0,
             "уже есть — бампни версию в PluginInfo.cs")
        if failures:
            sys.exit(1)

    # ---------- 1. Снимок master → main (без checkout: индекс во временном файле) ----------
    code, master_sha = run(["git", "-C", ROOT, "rev-parse", "master"])
    master_sha = master_sha.strip()
    step("master найден", code == 0, master_sha[:9])

    index = os.path.join(ROOT, ".git", "tmp-publish-index")
    env = dict(os.environ, GIT_INDEX_FILE=index)
    subprocess.run(["git", "-C", ROOT, "read-tree", "master"], env=env, check=True)
    for path in EXCLUDED:  # пути может уже не быть в индексе — это не ошибка
        subprocess.run(["git", "-C", ROOT, "rm", "-r", "-q", "--cached", path],
                       env=env, capture_output=True)
    p = subprocess.run(["git", "-C", ROOT, "ls-files", "--"] +
                       [path + "/" for path in EXCLUDED], env=env, capture_output=True)
    leftover = p.stdout.decode().strip()
    step("Исключаемые пути убраны из снимка", leftover == "",
         leftover.splitlines()[0] if leftover else ", ".join(EXCLUDED))
    tree = subprocess.run(["git", "-C", ROOT, "write-tree"], env=env,
                          check=True, capture_output=True).stdout.decode().strip()
    os.remove(index)

    code, prev = run(["git", "-C", ROOT, "rev-parse", "--verify", "main"])
    parents = ["-p", prev.strip()] if code == 0 else []
    code, subject = run(["git", "-C", ROOT, "log", "-1", "--format=%s", "master"])
    msg = "Supreme Nuclear v%s — публикация\n\n%s" % (version, subject.strip())
    new_main = subprocess.run(
        ["git", "-C", ROOT, "commit-tree", tree] + parents + ["-m", msg],
        check=True, capture_output=True).stdout.decode().strip()
    subprocess.run(["git", "-C", ROOT, "branch", "-f", "main", new_main], check=True)
    step("Снимок main создан", True, "%s (родитель: %s)"
         % (new_main[:9], (prev.strip()[:9] if parents else "нет — первый)")))

    # ---------- 2. Сборка артефакта в изолированном worktree ----------
    run(["git", "-C", ROOT, "worktree", "remove", "--force", WORKTREE])
    if os.path.exists(WORKTREE):
        shutil.rmtree(WORKTREE, ignore_errors=True)
    code, out = run(["git", "-C", ROOT, "worktree", "add", "--detach", WORKTREE, "main"])
    step("Worktree создан", code == 0, WORKTREE)

    if not failures:
        try:
            code, out = run(["dotnet", "build", os.path.join("src", "SupremeNuclear",
                        "SupremeNuclear.csproj"), "-c", "Release", "-p:Deploy=false"], cwd=WORKTREE)
            step("Сборка main 0 ошибок", code == 0 and ("Ошибок: 0" in out or "0 Error" in out),
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
            step("Локали снимка", code == 0)

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
    code, out = run(["git", "-C", ROOT, "push", "-u", "origin", "main"])
    step("Пуш main", code == 0, out[-200:] if code else "")
    notes = "Автопубликация из master: %s" % subject.strip()
    code, out = run(["gh", "release", "create", "v" + version, "--target", "main",
                     zip_path, "--title", "v" + version, "--notes", notes], cwd=ROOT)
    step("Релиз создан", code == 0, out[-200:] if code else "releases/tag/v" + version)
    if failures:
        print("\nПУШ/РЕЛИЗ ПРОВАЛЕН: " + ", ".join(failures))
        sys.exit(1)
    print("\nОПУБЛИКОВАНО: v%s — игроки качают из релизов (latest)" % version)


if __name__ == "__main__":
    main()

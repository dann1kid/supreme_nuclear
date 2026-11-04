# -*- coding: utf-8 -*-
"""Импорт стратегических иконок Supreme Commander FA в репозиторий мода.

Источник — набор «Advanced strategic icons» в gamedata пользовательской
установки SupCom FA (.scd — zip-архив; берутся варианты *_rest).
ВНИМАНИЕ: это сторонние арт-ассеты сообщества SupCom — дистрибуция с модом
решается владельцем репозитория (для личного использования — без вопросов).

Запуск:  python tools/import_supcom_icons.py "<путь к scd>"   (по умолчанию — G:)
Результат: icons/supcom/icon_<имя>.png (для мода: BepInEx/plugins/SupremeNuclear/Icons/supcom/)
"""
import io
import os
import re
import sys
import zipfile

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "icons", "supcom")
DEFAULT_SCD = r"G:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\gamedata\Advanced strategic icons.scd"
PREFIX = "textures/ui/common/game/strategicicons/"
NAME_RE = re.compile(r"icon_(.+?)_(rest|over|selected|selectedover)\.dds$")


def main():
    scd = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SCD
    if not os.path.exists(scd):
        print("Нет файла: " + scd)
        sys.exit(1)
    os.makedirs(OUT_DIR, exist_ok=True)
    z = zipfile.ZipFile(scd)
    converted = skipped = 0
    for name in z.namelist():
        if not name.startswith(PREFIX) or not name.endswith(".dds"):
            continue
        m = NAME_RE.match(name[len(PREFIX):])
        if not m or m.group(2) != "rest":
            continue
        stem = m.group(1)
        img = Image.open(io.BytesIO(z.read(name))).convert("RGBA")
        out_path = os.path.join(OUT_DIR, "icon_" + stem + ".png")
        img.save(out_path, "PNG")
        converted += 1
    # мусор от прошлого прогона не оставляем: PNG без пары в SCD — ошибка источника
    valid = {"icon_" + m.group(1) + ".png" for m in
             (NAME_RE.match(n[len(PREFIX):]) for n in z.namelist() if n.startswith(PREFIX))
             if m}
    for old in os.listdir(OUT_DIR):
        if old.endswith(".png") and old not in valid:
            os.remove(os.path.join(OUT_DIR, old))
            skipped += 1
    print("Конвертировано: %d, убрано лишних: %d -> %s" % (converted, skipped, OUT_DIR))


if __name__ == "__main__":
    main()

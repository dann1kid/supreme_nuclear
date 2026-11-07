# -*- coding: utf-8 -*-
"""Генерация ядерных иконок носителей/платформ (v0.154, юзер: «чётко понятно,
что связано»): кольцо-трёхлистник в палитре канона FA (icon_structure3_missile —
стратегический пусковой) на силуэтах из набора Advanced strategic icons.

Композиция собирается из ПИКСЕЛЕЙ набора (icons/supcom/) — стиль совпадает с
остальными иконками гарантированно; кольцо рисуется в канонической палитре
(красный/жёлтый/белый/чёрный — сэмплировано с structure3_missile). Результат —
icons/custom/icon_<stem>.png (НЕ трогаются чисткой import_supcom_icons.py,
деплой csproj → Icons/custom/). Стемы: structure_nuke (Хранилище БЧ),
land_nuke (ПУ с ядерной станцией), ship_nuke (корабль-носитель),
bomber_nuke / fighter_nuke (я-авиация).

Запуск:  python tools/make_nuke_icons.py   (после import_supcom_icons.py)
"""
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC_DIR = os.path.join(ROOT, "icons", "supcom")
OUT_DIR = os.path.join(ROOT, "icons", "custom")

# Палитра канона FA (сэмплировано с icon_structure3_missile).
RED = (255, 0, 0, 255)
YELLOW = (255, 255, 0, 255)
YELLOW_HI = (255, 255, 148, 255)   # светлый жёлтый (градиент поля канона)
WHITE = (255, 255, 255, 255)
BLACK = (8, 8, 8, 255)
GRAY = (148, 148, 148, 255)


def trefoil_ring(diameter):
    """Кольцо-трёхлистник в каноне FA (срезы structure3_missile): красная
    обводка → жёлтое кольцо → СЕРЫЙ диск → чёрный трёхлистник (лопасти ±30°,
    одна вниз + хаб). Геометрия рисуется с суперсэмплингом SS=6 и
    квантуется NEAREST — чёткие лопасти в пиксельном стиле на 14-20px."""
    SS = 6
    n = diameter
    big = n * SS
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    c = (big - 1) / 2.0
    d.ellipse([0, 0, big - 1, big - 1], fill=RED)
    y1 = 2 * SS
    d.ellipse([y1, y1, big - 1 - y1, big - 1 - y1], fill=YELLOW)
    f1 = 3.4 * SS
    d.ellipse([f1, f1, big - 1 - f1, big - 1 - f1], fill=GRAY)
    fr = c - f1
    br = fr * 0.82                  # лопасти до 82% поля
    for center in (30, 150, 270):
        d.pieslice([c - br, c - br, c + br, c + br],
                   center - 30, center + 30, fill=BLACK)
    hub = fr * 0.30
    d.ellipse([c - hub, c - hub, c + hub, c + hub], fill=BLACK)
    gap = fr * 0.30                 # белая кромка хаба (в каноне)
    d.ellipse([c - gap, c - gap, c + gap, c + gap], outline=WHITE,
              width=SS // 2)
    return img.resize((n, n), Image.NEAREST)


def load(name):
    path = os.path.join(SRC_DIR, "icon_" + name + ".png")
    if not os.path.exists(path):
        raise SystemExit("Нет источника: " + path)
    return Image.open(path).convert("RGBA")


def paste_center(base, mark, cx, cy):
    """Вставить mark по центру (cx, cy) с сохранением пиксельной сетки."""
    x = int(round(cx - mark.width / 2.0))
    y = int(round(cy - mark.height / 2.0))
    base.alpha_composite(mark, (x, y))
    return base


def make(stem, source, ring_px, cx, cy):
    """Силаэт источника + кольцо-трёхлистник в точке (cx, cy)."""
    base = load(source)
    mark = trefoil_ring(ring_px)
    paste_center(base, mark, cx, cy)
    out = os.path.join(OUT_DIR, "icon_" + stem + ".png")
    base.save(out, "PNG")
    print("собрано: %s (%s + кольцо %d в (%.1f, %.1f))" %
          (stem, source, ring_px, cx, cy))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    # Хранилище БЧ: корпус structure3_generic (квадрат 9..27 x 9..26 + бары).
    make("structure_nuke", "structure3_generic", 17, 17.5, 17.5)
    # ПУ с ядерной станцией: ромб land2_missile (9..27 x 9..31, ракета в центре).
    make("land_nuke", "land2_missile", 18, 17.5, 16)
    # Корабль-носитель: ship1_generic (кузов 8..28 x 10..30), кольцо на надстройке.
    make("ship_nuke", "ship1_generic", 18, 17.5, 15)
    # Я-авиация: бомбер и истребитель — кольцо на центре силуэта.
    make("bomber_nuke", "bomber1_generic", 18, 17.5, 17)
    make("fighter_nuke", "fighter1_generic", 15, 17.5, 16)


if __name__ == "__main__":
    main()

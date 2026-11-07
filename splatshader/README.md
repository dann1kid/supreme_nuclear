# splat.bundle — тайловый сплат-шейдер рельефа (канон SupCom/Neroxis)

Игра: Nuclear Option, **Unity 2022.3.62f2** — ревизия КРИТИЧНА. Бандл, собранный
даже соседней f1, рантайм f2 молча отвергает («not compatible with this newer
version of the Unity runtime», мод уезжает в гибрид — NRE-маска 2026-11-05,
плейтест: f1-бандлы не грузились НИКОГДА). Проверяй версию игры перед сборкой:
`grep -aoh "2022.3.62f[0-9]" NuclearOption_Data/globalgamemanagers`.
Без бандла мод продолжает работать на гибриде «по карте высот» — сборка
опциональна, но именно она снимает потолок детализации (тайлы ~метры вместо
5 м/тексель).

## Сборка (один раз, ~10 минут)

1. **Unity Hub** → установить редактор **точно 2022.3.62f2** (changeset
   `7670c08855a9`; установщик — download.unity3d.com/download_unity/7670c08855a9/…;
   модуль Windows Build Support не нужен — бандл не билд-таргет).
2. Новый проект → шаблон **Universal 3D** (в проекте будет URP 14 — инклуды
   шейдера `com.unity.render-pipelines.universal/...` резолвятся при импорте).
3. Скопировать в `Assets/` проекта:
   - `SplatTerrain.shader` (из этой папки);
   - `Editor/BuildSplatBundle.cs` (в `Assets/Editor/`).
4. Дождаться импорта: **в консоли не должно быть ошибок шейдера**
   (двойной клик по шейдеру — превью-сфера; ошибки правим в этой папке репо
   и копируем заново).
5. Меню **Supreme Nuclear → Build splat.bundle** → в корне проекта появится
   `splat.bundle`.
6. Положить `splat.bundle` в `BepInEx/plugins/SupremeNuclear/` игры.

## Проверка в игре

Перезапуск, сгенерировать карту. В `BepInEx/LogOutput.log`:

```
MapGen: сплат-шейдер загружен — SupremeNuclear/SplatTerrain (бандл N КБ)
MapGen: сплат-маска 2048² готова
MapGen: сплат-тайлы 4×512² готовы
MapGen: материал рельефа — СПЛАТ: макро 16384², маска 2048², тайлы 512²×4 (нормаль field_n)
```

Нет этих строк и написано «сплат недоступен (файл не найден …)» — бандл не
доложили; «шейдер не поддержан» — версия редактора не та.

## Ловушки сборки (все пойманы 2026-11-05, плейтест-пробником)

1. **Ревизия редактора = ревизия игры** (f2 ≠ f1 — см. шапку).
2. **Batch-сборка**: без `EditorUserBuildSettings.SwitchActiveBuildTarget(...)`
   BuildPipeline не пишет в бандл объект-контейнер AssetBundle («module AssetBundle
   is disabled in the build») — LoadFromFile отвергает такой файл. Фикс вшит в
   `Editor/BuildSplatBundle.cs`.
3. **Тег "RenderPipeline" = "UniversalPipeline"** в SubShader-тегах: у шейдера из
   бандла даёт isSupported=false (в редакторе и в игре). Тег убран — URP рисует
   единственный SubShader как обычно. Без мода-регресса: LoadFromFile OK +
   supported=true (пробник 2026-11-05, RTX 5090/D3D11).

## Ловушки сборки (все пойманы 2026-11-05/06, плейтест-пробником)

1. **Ревизия редактора = ревизия игры** (f2 ≠ f1 — см. шапку).
2. **Batch-сборка**: без `EditorUserBuildSettings.SwitchActiveBuildTarget(...)`
   BuildPipeline не пишет в бандл объект-контейнер AssetBundle — LoadFromFile
   отвергает такой файл. Фикс вшит в `Editor/BuildSplatBundle.cs`.
3. **BuildAssetBundles оставляет в шейдере только ПЕРВЫЙ пасс** (бисект 5
   вариантов, 2026-11-06: passCount=1 у всех). Поэтому шейдер ОДНОПАССОВЫЙ
   и БЕЗ тега LightMode (= SRPDefaultUnlit — URP рендерит его в forward,
   forward+ и deferred-фолбэке; игра = deferred, UniversalForward там не
   рисуется вовсе — «пропавший рельеф»).
4. **Текстуры с авторскими sRGB-цветами** (hex-палитра) — только с флагом
   sRGB (linear:false, `MapTextures.AlbedoTexture`); с linear:true GPU не
   декодирует и альбедо × свет вымывается в белый/лайм.
5. Тег "RenderPipeline"="UniversalPipeline" у шейдера из бандла давал
   isSupported=false — тег не ставим (URP рисует SubShader без него).

## Фото-страты (v0.155)

`Textures/` — 4 альбедо CC0 с Polyhaven (grass_ground / coast_sand_03 /
aerial_rocks_02 / snow_02, 2K jpg) — входят в бандл. Импортёр: readable +
sRGB=false (множительная семантика). На загрузке мод нормализует среднюю
яркость к 0.86 (света ≤ 0.98) и кладёт в _Tile0..3; нет файла в бандле —
фолбэк на процедурные серые тайлы. Шейдер сэмплирует .rgb — серый тайл
даёт прежнюю семантику, фото — цветную фактуру.
Атрибуция: текстуры Poly Haven (polyhaven.com), лицензия CC0.

## Дистанционное смешение (v0.155)

Тексель макро — 5 м (16384² на 82 км, потолок GPU): на среднем зуме видна
его сетка. Шейдер смешивает по дистанции до камеры: <700 м — фото-тайл
доминирует (макро 30%), >7 км — макро 100% (стратег-вид сохраняет зоны).
Пороги — macroW в SplatFrag. Фото-тайл 2K на 4 м — резкость не ограничена.

6. **Хэш `frac(sin(dot(p,K)*43758))` на мировых координатах** — у GPU range
   reduction синуса ломается на аргументах >1e5 (мир ±41 км × частота) →
   NaN → альбедо NaN → буфер+bloom рисуют белое ПЯТНО-«фонарь» ровно там,
   где включено зерно (ближе 900 м). Микрозерно использует бес-синусовый
   hash21 (Dave Hoskins) + сворачивание координат mod 4096 м.

## Что внутри (для правок)

- `SplatTerrain.shader` — ForwardLit (+ShadowCaster/DepthOnly/DepthNormals);
  сэмплит всё **глобальными** координатами (`world − _Datum_OriginPosition`),
  поэтому не зависит от UV меша и сдвигов FloatingOrigin. Слои:
  `_MacroMap` (наш albedo-зоны, hue) × маска `_MaskMap` (RGBA =
  трава/песок/скала/снег) × тайлы `_Tile0..3` (grayscale-фактура, период
  `_TileMetres` м × вторая октава `_Tile2Metres` м) + нормал-деталь `_BumpMap`
  (`_BumpMetres`) + выжженное `_Global_BlastMap` (глобал игры, сила
  `_BlastStrength` — ставит мод при живом BlastManager) + приём теней
  `_SN_RECEIVE_SHADOWS` (эксперимент, `MapGen.TerrainShadows`; каст рельефа
  всегда off).
- Маску и тайлы мод генерит сам (`MapTextures.SplatMask/SplatTiles`,
  процедурно, детерминированно) — в бандле ТОЛЬКО шейдер.

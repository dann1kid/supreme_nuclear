using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Асинхронная генерация «Скримиша» (v0.154, лобби): канва стоит только
    /// на неустранимых движковых хичах, вся математика уводится с главного
    /// потока. Две фазы: (1) ВОРКЕР — мир ScenarioWorld.Generate + пиксели
    /// текстур (чистая математика, MapTextures.ComputePixels); (2) СБОРКА —
    /// ступени MapBuildJob размазываются по кадрам с бюджетом времени
    /// (FrameBudgetMs): превью появляется сразу после воркера, меш/коллайдеры
    /// докатываются фоном. Результат — построенная карта в кэше RuntimeMap +
    /// сейв миссии (предгенерация: «Создать лобби»/«В БОЙ» стартуют мгновенно).
    /// Отмена — счётчик поколений: новый запрос обесценивает старый.
    /// </summary>
    internal sealed class MapGenAsync : MonoBehaviour
    {
        internal enum Phase { Idle, Worker, Building, Done, Failed }

        /// <summary>Бюджет главного потока на кадр в фазе сборки, мс: ступень
        /// не начинается, если лимит исчерпан (текущая ступень не прерывается —
        /// движковые вызовы неделимы; самый долгий — Apply albedo).</summary>
        private const float FrameBudgetMs = 8f;

        private static MapGenAsync _i;
        private Phase _phase;
        private bool _buildMission = true;          // false — гость (только карта+превью)
        private bool _loadEditor;                   // редакторский Ctrl+G: после сейва LoadEditor
        private int _generation;                    // отменa: новое поколение старше
        private ScenarioBuild.Prep _prep;
        private MapBuildJob _job;
        private IEnumerator<float> _steps;
        private volatile bool _workerDone;
        private int _workerGen;                     // поколение завершившегося воркера
        private string _workerError;
        private double _workerStartMs;              // ETA-самокалибровка бара
        private GUIStyle _barLabel;                 // кэш стилей OnGUI (правило 8)
        private string _barText;                    // строка лейбла — только при смене %
        private int _barPct = -1;
        private Phase? _barPhase;

        /// <summary>Длительность воркера прошлой генерации, с — ETA бара
        /// (самокалибруется; первая генерация — оценка 60 с).</summary>
        internal static double LastWorldSeconds = 60;

        /// <summary>Генерация идёт (воркер/сборка) — гейт повторного Ctrl+G.</summary>
        internal static bool Busy =>
            _i != null && (_i._phase == Phase.Worker || _i._phase == Phase.Building);

        internal static Phase Current => _i != null ? _i._phase : Phase.Idle;
        internal static float Progress { get; private set; }   // 0..1
        internal static string ErrorText { get; private set; }
        internal static Sprite Preview { get; private set; }   // превью панели
        internal static string TemplateName { get; private set; }
        internal static int Seed { get; private set; }
        internal static int Players { get; private set; }
        internal static string SaveName { get; private set; }  // готово: имя миссии
        internal static string MapKey { get; private set; }

        /// <summary>Событие смены состояния — панель перерисовывает статус/превью.</summary>
        internal static event Action Changed;

        private void Awake()
        {
            _i = this;
        }

        private void OnDestroy()
        {
            if (_i == this)
                _i = null;
        }

        /// <summary>Редакторский Ctrl+G: та же конвейерная лента, после сейва —
        /// LoadEditor (раскладка перезагружается в редактор, как sync-путь).
        /// players — из шаблона (легаси-поведение ScenarioBuild.Generate).</summary>
        internal static bool RequestForEditor(string templateName, int seed)
        {
            if (_i == null || Busy)
                return false;
            var tpl = ScenarioTemplate.Load(templateName);
            bool started = Request(templateName, seed, tpl != null ? tpl.players : 2,
                buildMission: true);
            if (started)
                _i._loadEditor = true;
            return started;
        }

        /// <summary>Прогресс-бар генерации (IMGUI поверх всего, любой стейт —
        /// меню/редактор). Без аллокаций на вызов: лейбл пересобирается только
        /// при смене целого процента/этапа (правило 8), стили кэшуются.</summary>
        private void OnGUI()
        {
            if (_phase != Phase.Worker && _phase != Phase.Building)
                return;
            if (_barLabel == null)
            {
                _barLabel = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 15,
                };
            }
            float w = 540, h = 60;
            var r = new Rect((Screen.width - w) / 2f, Screen.height - h - 48f, w, h);
            GUI.Box(r, GUIContent.none);

            int pct = (int)(Progress * 100f);
            if (pct != _barPct || _barPhase != _phase)
            {
                _barPct = pct;
                _barPhase = _phase;
                _barText = _phase == Phase.Worker
                    ? Loc.F("Генерация мира: {0}%", pct)
                    : Loc.F("Сборка карты: {0}%", pct);
            }
            GUI.Label(new Rect(r.x, r.y + 4f, w, 24f), _barText, _barLabel);
            var bar = new Rect(r.x + 14f, r.y + 34f, w - 28f, 14f);
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = new Color(0.30f, 0.78f, 0.35f);
            GUI.DrawTexture(new Rect(bar.x + 2f, bar.y + 2f,
                (bar.width - 4f) * Mathf.Clamp01(Progress), bar.height - 4f),
                Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        /// <summary>Запрос генерации (панель/лобби). supersede — перебить идущую
        /// (гость вслед за сменой карты хостом); buildMission=false — только карта
        /// + превью (гостю: миссия приезжает по сети, сейв не нужен).</summary>
        internal static bool Request(string templateName, int seed, int players,
            bool buildMission = true, bool supersede = false,
            System.Collections.Generic.List<LobbyState.Army> armies = null, string victory = null)
        {
            if (_i == null)
                return false;
            if (!supersede && (_i._phase == Phase.Worker || _i._phase == Phase.Building))
                return false;
            players = LobbyState.ClampPlayers(players);
            var p = new ScenarioBuild.Prep();
            // Шаблон/игроки/сид/ключ/фракции — главный поток (SystemInfo/Random/ростр).
            if (!ScenarioBuild.PrepareForAsync(templateName, seed, players, p, out string error))
            {
                Fail(error, templateName, p.Seed, players);
                return true;
            }
            // Лобби v3: армии-слоты (HQ на позициях пула, N фракций миссии) и
            // условие победы (захват строит objectives; иначе VictoryService).
            p.Armies = armies;
            if (!string.IsNullOrEmpty(victory))
                p.Victory = victory;
            int gen = ++_i._generation;
            _i.ResetResult();
            _i._prep = p;
            _i._buildMission = buildMission;
            _i._loadEditor = false;
            _i._workerStartMs = Time.realtimeSinceStartup * 1000.0;
            _i._phase = Phase.Worker;
            TemplateName = templateName;
            Seed = p.Seed;
            Players = players;
            MapKey = p.TerrainKey;
            Progress = 0.05f;
            Log.Info($"MapGenAsync: запрос — шаблон {templateName}, сид {p.Seed}, игроков {players}, " +
                     $"ключ {p.TerrainKey}");
            Task.Run(() =>
            {
                try
                {
                    ScenarioBuild.RunWorldPart(p); // чистая математика, без Unity-объектов
                }
                catch (Exception e)
                {
                    _i._workerError = e.GetType().Name + ": " + e.Message;
                    Log.Error("MapGenAsync: воркер упал — " + _i._workerError + "\n" + e.StackTrace);
                }
                _i._workerDone = true;
                _i._workerGen = gen;
            });
            Changed?.Invoke();
            return true;
        }

        /// <summary>Сброс результата (новый запрос/закрытие панели) — превью в топку.</summary>
        private void ResetResult()
        {
            if (Preview != null)
            {
                Destroy(Preview.texture); // спрайт владеет текстурой 1024²
                Destroy(Preview);
            }
            Preview = null;
            SaveName = null;
            ErrorText = null;
        }

        /// <summary>Готовая генерация отвечает этим параметрам? (гейт СТАРТ/В БОЙ).</summary>
        internal static bool Matches(string templateName, int seed, int players)
        {
            return Current == Phase.Done && TemplateName == templateName &&
                   Seed == seed && Players == LobbyState.ClampPlayers(players);
        }

        private static void Fail(string error, string templateName, int seed, int players)
        {
            if (_i == null)
                return;
            _i._phase = Phase.Failed;
            _i.ResetResult();
            ErrorText = error;
            TemplateName = templateName;
            Seed = seed;
            Players = players;
            Changed?.Invoke();
        }

        private void Update()
        {
            try
            {
                // Дожим отложенной инициализации лобби: NetworkManagerNuclearOption
                // грузится префабом ПОЗЖЕ плагина (плейтест 2026-11-06) — подписки
                // сети/сообщений ставим, как только менеджер появится. Дёшево: bool.
                if (!SnLobbyService.Initialized)
                    SnLobbyService.EnsureInitialized();
                Pump();
            }
            catch (Exception e)
            {
                Log.Error("MapGenAsync: сбой драйвера — " + e);
                _phase = Phase.Failed;
                ErrorText = Loc.F("Генерация сорвалась: {0}", e.Message);
                Changed?.Invoke();
            }
        }

        private void Pump()
        {
            switch (_phase)
            {
                case Phase.Worker:
                    // ETA-самокалибровка: бар ползёт по длительности прошлой
                    // генерации (мир — самый долгий полюс на 8192²)
                    double etaMs = System.Math.Max(10.0, LastWorldSeconds) * 1000.0;
                    double elapsedMs = Time.realtimeSinceStartup * 1000.0 - _workerStartMs;
                    Progress = 0.05f + 0.30f * Mathf.Clamp01((float)(elapsedMs / etaMs));
                    if (!_workerDone)
                        return;
                    _workerDone = false;
                    LastWorldSeconds = (Time.realtimeSinceStartup * 1000.0 - _workerStartMs) / 1000.0;
                    if (_workerGen != _generation)
                        return; // обесценено новым запросом
                    if (_workerError != null)
                    {
                        _phase = Phase.Failed;
                        ErrorText = Loc.F("Генерация мира сорвалась: {0}", _workerError);
                        _workerError = null;
                        Changed?.Invoke();
                        return;
                    }
                    // Воркер готов: превью — сразу (главный поток, 1024² — дёшево).
                    Preview = MapTextures.MapSpriteFromPixels(_prep.Pixels.Map1024);
                    if (RuntimeMap.IsBuilt(_prep.TerrainKey))
                    {
                        // Карта уже в кэше (та же карта повторно) — сборка не нужна.
                        FinishBuild();
                        return;
                    }
                    _job = new MapBuildJob(_prep.TerrainKey, ScenarioBuild.HqPositions(_prep),
                        _prep.World, _prep.Pixels);
                    _steps = _job.Steps().GetEnumerator();
                    _phase = Phase.Building;
                    Progress = 0.35f;
                    Changed?.Invoke();
                    return;

                case Phase.Building:
                    float startMs = Time.realtimeSinceStartup * 1000f;
                    bool moved = false;
                    do
                    {
                        if (!_steps.MoveNext())
                        {
                            FinishBuild();
                            return;
                        }
                        moved = true;
                        // Ступени дают 0..1 сборки — мапим на 0.35..1.0 общего прогресса.
                        Progress = 0.35f + 0.65f * Mathf.Clamp01(_steps.Current);
                    }
                    while (Time.realtimeSinceStartup * 1000f - startMs < FrameBudgetMs);
                    if (moved)
                        Changed?.Invoke();
                    return;
            }
        }

        /// <summary>Сборка карты готова (или не нужна) — миссия-JSON и сейв.</summary>
        private void FinishBuild()
        {
            _steps = null;
            if (_job != null && !_job.Success)
            {
                _job = null;
                _phase = Phase.Failed;
                ErrorText = Loc.Tr("Не удалось построить спец-карту — подробности в логе");
                Changed?.Invoke();
                return;
            }
            _job = null;
            if (_buildMission)
            {
                bool ok;
                string result;
                if (_loadEditor)
                {
                    ok = ScenarioBuild.MissionFromPrebuiltEditor(_prep, out result, out string saveName);
                    SaveName = saveName;
                    if (ok)
                        CommanderHud.Notice(result); // «раскладка загружается в редактор»
                }
                else if (!ScenarioBuild.MissionFromPrebuilt(_prep, out result, out string saveName))
                {
                    ok = false;
                }
                else
                {
                    SaveName = saveName;
                    ok = true;
                }
                if (!ok)
                {
                    _phase = Phase.Failed;
                    ErrorText = result;
                    Changed?.Invoke();
                    return;
                }
            }
            _phase = Phase.Done;
            Progress = 1f;
            Log.Info("MapGenAsync: готово — " + (SaveName ?? MapKey) +
                     (_buildMission ? " (предгенерация: старт мгновенный)" : " (карта гостя)"));
            Changed?.Invoke();
        }
    }
}

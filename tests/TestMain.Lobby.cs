using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: MP-лобби.
    internal static partial class TestMain
    {
        // MP-лобби (LobbyState, ключи, сиды, пиксельный срез) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsLobby()
        {
            // ---------- MP-лобби Ур.2 (v0.154): LobbyState + пиксельный срез ----------
            {
                // Лобби v3 (армии-слоты/команды/позиции/победа): round-trip и гейты.
                {
                    var st = new LobbyState
                    {
                        victory = LobbyState.VictoryAnnihilate,
                        hostId = 1,
                        pool = 4,
                        armies = LobbyState.ArmiesForPool(4),
                        map = new LobbyState.MapInfo { template = "duel-land", seed = 42, players = 4, saveName = "SN-Terrain-duel-land-0000002A" },
                    };
                    st.armies[1].controller = LobbyState.CtrlAi;
                    st.armies[2].controller = LobbyState.CtrlOpen;
                    st.armies[3].controller = LobbyState.CtrlClosed;
                    st.armies[2].team = 1; // союз с армией 1
                    st.armies[0].humanId = 1; // хост в слоте 1
                    st.members.Add(new LobbyState.Member { id = 7, name = "ace" });
                    var back = LobbyState.FromJson(st.ToJson());
                    string dbg = back == null ? "null"
                        : "v=" + back.v + " n=" + back.armies.Count +
                        " vic=" + back.victory + " c1=" + back.armies[1].controller +
                        " t2=" + back.armies[2].team + " c3=" + back.armies[3].controller +
                        " h0=" + back.armies[0].humanId + " m=" + back.members.Count +
                        " host=" + back.hostId + " map=" + (back.map != null);
                    Check("LobbyState v3: round-trip (армии/команды/победа)",
                        back != null && back.victory == LobbyState.VictoryAnnihilate &&
                        back.armies.Count == 4 &&
                        back.armies[1].controller == LobbyState.CtrlAi &&
                        back.armies[2].team == 1 &&
                        back.armies[3].controller == LobbyState.CtrlClosed &&
                        back.members.Count == 1 && back.hostId == 1, dbg);
                    Check("LobbyState v3: живые армии/соло/наблюдатели",
                        LobbyState.LiveArmies(st.armies) == 3 &&
                        LobbyState.Observers(st).Count == 1 && LobbyState.Observers(st)[0].id == 7);
                    var solo = new LobbyState { armies = LobbyState.ArmiesForPool(2), map = new LobbyState.MapInfo { players = 2 } };
                    solo.armies[1].controller = LobbyState.CtrlClosed;
                    Check("LobbyState v3: соло → бесконечная победа",
                        LobbyState.EffectiveVictory(solo) == LobbyState.VictoryEndless &&
                        LobbyState.EffectiveVictory(st) == LobbyState.VictoryAnnihilate);
                    Check("LobbyState v3: союз по команде",
                        LobbyState.Allied(st.armies[0], st.armies[2]) &&
                        !LobbyState.Allied(st.armies[0], st.armies[1]) &&
                        !LobbyState.Allied(st.armies[0], st.armies[3]));
                    Check("LobbyState v3: валидация к старту (позиции/люди/пул)",
                        LobbyState.ValidateForLaunch(st) == null &&
                        LobbyState.ValidateForLaunch(new LobbyState()) != null && // нет карты
                        true);
                    // Библиотека карт: парс имён SN-сейвов (лобби v3).
                {
                    SkirmishService.SavedMap ok = SkirmishService.ParseSavedName(
                        "SN-Terrain-Duel-Land-00AB12CD");
                    Check("Библиотека: валидное имя сейва парсится",
                        ok != null && ok.Template == "Duel-Land" && ok.Seed == 0x00AB12CD &&
                        ok.Players == -1); // легаси-ключ без «-pN»
                    SkirmishService.SavedMap withP = SkirmishService.ParseSavedName(
                        "SN-Terrain-Duel-Coast-00112233-AB12CD34-80k-p4");
                    Check("Библиотека: ключ v3 с пулом и морфологией",
                        withP != null && withP.Players == 4 &&
                        withP.Template == "Duel-Coast" && withP.Seed == 0x00112233);
                    Check("Библиотека: чужие имена игнорируются",
                        SkirmishService.ParseSavedName("SN-Duel-00AB12CD") == null &&
                        SkirmishService.ParseSavedName("MyMission") == null &&
                        SkirmishService.ParseSavedName(null) == null);
                }
                Check("LobbyRequest v3: round-trip слота",
                        LobbyRequest.FromJson(new LobbyRequest { req = LobbyRequest.TakeSlot, armyId = 2 }.ToJson())
                            is LobbyRequest rq && rq.req == LobbyRequest.TakeSlot && rq.armyId == 2);
                }


                // Пиксельный срез (воркер MapGenAsync) — чистая математика:
                // дважды от одного мира = байт-в-байт тот же буфер 1024².
                var tpl = new ScenarioTemplate();
                tpl.players = 2;
                tpl.terrain.sizeM = 24000f;
                tpl.terrain.stepM = 200f;
                WorldGen w = ScenarioWorld.Generate(tpl, 4242);
                UnityEngine.Color32[] px1 = MapTextures.ComputePixels(w.Hm, tpl.terrain, 1024).Map1024;
                UnityEngine.Color32[] px2 = MapTextures.ComputePixels(w.Hm, tpl.terrain, 1024).Map1024;
                bool pixelsSame = px1.Length == px2.Length;
                if (pixelsSame)
                    for (int i = 0; i < px1.Length; i++)
                        if (px1[i].r != px2[i].r || px1[i].g != px2[i].g ||
                            px1[i].b != px2[i].b || px1[i].a != px2[i].a)
                        {
                            pixelsSame = false;
                            break;
                        }
                Check("MapGenAsync: пиксельный срез детерминирован (1024², воркер==главный)",
                    pixelsSame, px1.Length + " пикселей");
            }
        }
    }
}

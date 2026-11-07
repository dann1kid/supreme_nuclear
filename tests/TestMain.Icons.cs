using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: иконки и дрессинг.
    internal static partial class TestMain
    {
        // стратегические/ядерные иконки, дрессинг ScatterPlan (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsIconsAndScatter()
        {
            // ---------- StrategicIcons.StemCandidates: стемы стратегических иконок FA ----------
            var stemT1 = StrategicIcons.StemCandidates("land_directfire", 1);
            Check("StemCandidates: tiered первым, plain вторым",
                stemT1.Length == 2 && stemT1[0] == "land1_directfire" && stemT1[1] == "land_directfire");
            var stemT3 = StrategicIcons.StemCandidates("land_engineer", 3);
            Check("StemCandidates: тир 3 в стеме", stemT3[0] == "land3_engineer");
            var stemClamp = StrategicIcons.StemCandidates("structure_mass", 7);
            Check("StemCandidates: тир клампится 1..3", stemClamp[0] == "structure3_mass");
            Check("StemCandidates: без роли — один plain-стем",
                StrategicIcons.StemCandidates("commander", 2).Length == 1 &&
                StrategicIcons.StemCandidates("commander", 2)[0] == "commander");

            // ---------- Ядерные иконки (v0.154): NukeStemOf/CandidatesOf ----------
            Check("NukeStemOf: класс категории → cls_nuke",
                StrategicIcons.NukeStemOf("ship_directfire") == "ship_nuke" &&
                StrategicIcons.NukeStemOf("bomber_generic") == "bomber_nuke" &&
                StrategicIcons.NukeStemOf("land_artillery") == "land_nuke");
            Check("NukeStemOf: ядерная категория идемпотентна",
                StrategicIcons.NukeStemOf("structure_nuke") == "structure_nuke");
            var nukeShip = StrategicIcons.CandidatesOf("ship_directfire", true, 2);
            Check("CandidatesOf: ядерный стем первым, базовые дальше",
                nukeShip.Length == 3 && nukeShip[0] == "ship_nuke" &&
                nukeShip[1] == "ship2_directfire" && nukeShip[2] == "ship_directfire");
            var nukeOff = StrategicIcons.CandidatesOf("land_artillery", false, 1);
            Check("CandidatesOf: без ЯО — базовый список без ядерного",
                nukeOff.Length == 2 && nukeOff[0] == "land1_artillery");

            // ---------- Util.TypeListContains: строгий матч списка типов профиля ----------
            Check("TypeListContains: вхождение по коду",
                Util.TypeListContains("DDG,LHA,CV", "CV") &&
                Util.TypeListContains("DDG, LHA,CV", "LHA"));
            Check("TypeListContains: без регистра и пробелов",
                Util.TypeListContains("ddg, lha", "DDG"));
            Check("TypeListContains: substring НЕ считается (короткие коды)",
                !Util.TypeListContains("DDG,LHA,CV", "DG") &&
                !Util.TypeListContains("DDG,LHA,CV", "HA") &&
                !Util.TypeListContains("CV", "CVN"));
            Check("TypeListContains: пустые — ложь",
                !Util.TypeListContains("", "CV") && !Util.TypeListContains("DDG", null) &&
                !Util.TypeListContains(null, "CV"));

            // ---------- ScatterPlan: дрессинг карты (деревья/шрамы/декали, v0.154) ----------
            {
                var dressT = new ScenarioTemplate.TerrainSettings
                {
                    sizeM = 16000f, stepM = 80f, edgeM = 2000f, coastFlatM = 0f,
                    treeDensity = 0.55f, wearAmount = 0.6f, wetBandM = 120f,
                    battleScars = 0.7f, decalAmount = 0.7f, symmetry = "mirror",
                };
                var dressHm = HeightmapGen.Generate(dressT, 4242);
                var dressWorld = new WorldGen(); // без раскладки — запреты только по маскам
                var dressC = ScatterPlan.From(dressHm, dressWorld, dressT);

                var treesA = ScatterPlan.Trees(dressC, dressT, 777);
                var treesB = ScatterPlan.Trees(dressC, dressT, 777);
                Check("ScatterPlan: деревья — детерминизм сида",
                    treesA.Count == treesB.Count && treesA.Count > 0,
                    "деревьев " + treesA.Count);
                bool treesDet = true;
                for (int i = 0; i < treesA.Count && treesDet; i++)
                    if (treesA[i].X != treesB[i].X || treesA[i].Z != treesB[i].Z)
                        treesDet = false;
                Check("ScatterPlan: деревья — координаты совпадают", treesDet);

                // Зеркальная симметрия X (рельеф mirror): каждый образ имеет пару.
                var treeSet = new HashSet<UnityEngine.Vector2>();
                foreach (var p in treesA)
                    treeSet.Add(new UnityEngine.Vector2(p.X, p.Z));
                bool treeSym = true;
                foreach (var p in treesA)
                    if (!treeSet.Contains(new UnityEngine.Vector2(-p.X, p.Z)))
                        treeSym = false;
                Check("ScatterPlan: деревья — зеркальные пары по X", treeSym);

                bool treeMask = true, treePadCorr = true;
                var noPads = new List<PadZone>();
                var noCorr = new List<CorridorSeg>();
                foreach (var p in treesA)
                {
                    if (!dressHm.SiteOk(p.X, p.Z))
                        treeMask = false;
                    if (ScatterPlan.InAnyPad(noPads, p.X, p.Z, 0f) ||
                        ScatterPlan.InAnyCorridor(noCorr, p.X, p.Z, 0f))
                        treePadCorr = false; // пустые списки — ничего не запрещают
                }
                Check("ScatterPlan: деревья — только на SiteOk (не вода/склон/край)", treeMask);
                Check("ScatterPlan: пустые запреты ничего не режут", treePadCorr);

                // Исключения: площадка и коридор режут позиции внутри своих зон.
                var pad = new PadZone { Center = UnityEngine.Vector2.zero, HalfA = 2000f, HalfB = 2000f };
                var seg = new CorridorSeg
                {
                    A = new UnityEngine.Vector2(-4000f, 1000f),
                    B = new UnityEngine.Vector2(4000f, 1000f),
                    HalfW = 120f,
                };
                var excWorld = new WorldGen();
                excWorld.Pads.Add(pad);
                excWorld.Corridors.Add(seg);
                var excC = ScatterPlan.From(dressHm, excWorld, dressT);
                var excTrees = ScatterPlan.Trees(excC, dressT, 777);
                bool exclOk = true;
                foreach (var p in excTrees)
                    if (pad.Contains(p.X, p.Z, 120f) ||
                        ScatterPlan.DistToSegXZ(p.X, p.Z, seg) <= seg.HalfW + 60f)
                        exclOk = false;
                Check("ScatterPlan: деревья не стоят в площадке (+120) и коридоре (+60)", exclOk,
                    "деревьев " + excTrees.Count);

                // Шрамы: ≥ 40 м радиус (порог DrawBlast после ×2), на суше.
                var scars = ScatterPlan.Scars(dressC, dressT, 777);
                bool scarsOk = scars.Count > 0;
                foreach (var s in scars)
                {
                    if (s.Radius < 40f)
                        scarsOk = false;
                    if (dressHm.SampleHeight(s.X, s.Z) < dressHm.SeaLevel + 3f)
                        scarsOk = false;
                }
                Check("ScatterPlan: шрамы — радиус ≥ 40 м и на суше", scarsOk,
                    "шрамов " + scars.Count);
                var scarsNoKnob = ScatterPlan.Scars(dressC,
                    new ScenarioTemplate.TerrainSettings { sizeM = 16000f, stepM = 80f, battleScars = 0f }, 777);
                Check("ScatterPlan: battleScars=0 — шрамов нет", scarsNoKnob.Count == 0);

                // Декали: не в воде (кромка 0–3 м легитимна — мокрая грязь),
                // радиусы в разумных границах, kind в словаре тинтов.
                var decals = ScatterPlan.Decals(dressC, dressT, 777);
                bool decalsOk = decals.Count > 0;
                foreach (var d in decals)
                {
                    if (d.Radius < 8f || d.Radius > 95f || d.Kind < 0 || d.Kind > 2)
                        decalsOk = false;
                    if (dressHm.SampleHeight(d.X, d.Z) < dressHm.SeaLevel)
                        decalsOk = false;
                }
                Check("ScatterPlan: декали — радиусы/кinds/суша", decalsOk,
                    "декалей " + decals.Count);

                // Выкл-ручки: ноль позиций без выбросов.
                var offT = new ScenarioTemplate.TerrainSettings
                {
                    sizeM = 16000f, stepM = 80f, edgeM = 2000f,
                    treeDensity = 0f, battleScars = 0f, decalAmount = 0f,
                };
                var offC = ScatterPlan.From(dressHm, dressWorld, offT);
                Check("ScatterPlan: все ручки в нуле — пустые списки",
                    ScatterPlan.Trees(offC, offT, 1).Count == 0 &&
                    ScatterPlan.Scars(offC, offT, 1).Count == 0 &&
                    ScatterPlan.Decals(offC, offT, 1).Count == 0);

                // Симметричные коридоры: mirror удваивает, концы отражаются парно.
                var oneSeg = new List<CorridorSeg>
                {
                    new CorridorSeg
                    {
                        A = new UnityEngine.Vector2(100f, 200f),
                        B = new UnityEngine.Vector2(300f, 400f),
                        HalfW = 90f,
                    },
                };
                var mirrored = ScatterPlan.SymmetricCorridors(oneSeg, "mirror");
                Check("ScatterPlan: mirror-коридоры ×2, парное отражение концов",
                    mirrored.Count == 2 &&
                    mirrored[1].A.x == -100f && mirrored[1].A.y == 200f &&
                    mirrored[1].B.x == -300f && mirrored[1].B.y == 400f &&
                    mirrored[1].HalfW == 90f);
                var quad = ScatterPlan.SymmetricCorridors(oneSeg, "quadrant");
                Check("ScatterPlan: quadrant-коридоры ×4", quad.Count == 4);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Mirage;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using Steamworks;
using UniTask = Cysharp.Threading.Tasks.UniTask;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SupremeNuclear
{
    /// <summary>
    /// MP-лобби v3 «как в SupCom» (единое для сингла и MP; комната до карты —
    /// флоу dedicated-сервера): хост поднимает Steam-лобби + сервер в EMPTY
    /// (ServerWaiting), входящие — в ПУЛ НАБЛЮДАТЕЛЕЙ (члены без слота), слоты
    /// берут сами («Взять слот»/«Покинуть») или хост назначает. Стол армий:
    /// контроллер closed/open/AI/human, команда-союз, позиция старта (пул баз
    /// генератора). Карта и УСЛОВИЕ ПОБЕДЫ (захват всех чужих баз / уничтожение
    /// / бесконечная; соло — одна живая армия — всегда бесконечная) выбираются
    /// в лобби. СТАРТ — рецепт LoadNext; фракции армий (N, рантайм —
    /// FactionFactory) применяет патч №16 при спавне; асы-лётчики входят
    /// нативно (Steam/браузер) после старта. Синк — SnLobbyMessage (JSON).
    /// </summary>
    internal static class SnLobbyService
    {
        /// <summary>Патч №15 держит MissionManager.StartMission (окно плана).</summary>
        internal static volatile bool HoldStart;

        internal static bool Hosting { get; private set; }
        internal static bool IsGuest { get; private set; }

        internal static bool InWaitingRoom =>
            Hosting ? State != null && State.status == LobbyState.StatusWaiting
                    : IsGuest && State != null && State.status == LobbyState.StatusWaiting;

        /// <summary>Хост: authoritative; гость: копия от хоста.</summary>
        internal static LobbyState State { get; private set; }

        private static Mission _mission;
        private static HostedLobbyInstance? _lobby;
        private static bool _launching;
        private static readonly HashSet<int> ReadyIds = new HashSet<int>();

        internal static event System.Action StateChanged;

        // ---------- инициализация/сброс ----------

        internal static bool _subscribed;

        internal static void EnsureInitialized()
        {
            if (_subscribed || !SnLobbyMessages.TryInitialize())
                return;
            var nm = NetworkManagerNuclearOption.i;
            if (nm == null)
                return;
            nm.Server.Stopped.AddListener(ResetSession); // AddLateEvent без аргументов
            nm.Client.Disconnected.AddListener(_ =>
            {
                if (!Hosting)
                    ResetSession();
            });
            nm.Server.Authenticated.AddListener(OnPlayerAuthenticated);
            nm.Server.Disconnected.AddListener(OnPlayerDisconnected);
            _subscribed = true;
            Log.Info("SnLobby: подписки сети готовы (менеджер найден)");
        }

        internal static void InitializeScenes()
        {
            SceneManager.sceneLoaded += (scene, _) =>
            {
                if (scene.name == MapLoader.MainMenu)
                    ResetSession();
            };
        }

        internal static bool Initialized => _subscribed;

        internal static void ResetSession()
        {
            if (!Hosting && !IsGuest && State == null)
                return;
            HoldStart = false;
            Hosting = false;
            IsGuest = false;
            _launching = false;
            _mission = null;
            _lobby = null;
            State = null;
            ReadyIds.Clear();
            Log.Info("SnLobby: сеанс лобби сброшен");
            StateChanged?.Invoke();
        }

        // ---------- хост: создание лобби ----------

        internal enum LobbyVisibility { Public, Friends, Private }

        /// <summary>Создание: Steam-лобби + сервер в EMPTY. Армии/карта/условия —
        /// настраиваются В лобби. Соло = то же лобби (одна живая армия).</summary>
        internal static async UniTask CreateLobbyAsync(string lobbyName, LobbyVisibility visibility,
            string password, int slots)
        {
            NetworkManagerNuclearOption.i.SetModdedServer(true);
            SteamLobby steamLobby = EnsureSteamLobby();
            if (steamLobby == null)
            {
                LobbyEvents.RaiseHostResult(false, Loc.Tr("Steam недоступен — лобби не создать"));
                return;
            }
            steamLobby.CheckRelayLocationTask();
            var type = visibility == LobbyVisibility.Public ? ELobbyType.k_ELobbyTypePublic
                : visibility == LobbyVisibility.Friends ? ELobbyType.k_ELobbyTypeFriendsOnly
                : ELobbyType.k_ELobbyTypePrivate;
            HostedLobbyInstance? hosted = await steamLobby.HostLobby(slots, type);
            if (hosted == null || !hosted.Value.IsValid)
            {
                LobbyEvents.RaiseHostResult(false, Loc.Tr("Steam не создал лобби — проверь соединение"));
                return;
            }
            _lobby = hosted;
            Hosting = true;
            bool hasPassword = !string.IsNullOrEmpty(password);
            hosted.Value.SetData("name", LobbyDisplayName(lobbyName).SanitizeRichText(128));
            hosted.Value.SetData("mission_name", ("SN: " + LobbyDisplayName(lobbyName)).SanitizeRichText(128));
            hosted.Value.SetData("short_password",
                hasPassword ? LobbyPassword.GetShortPassword(password) : "");
            hosted.Value.SetData("modded_server", LobbyInstance.BoolToTag(true));

            State = new LobbyState { armies = LobbyState.ArmiesForPool(2) }; // дефолт-пул 2
            ReadyIds.Clear();
            ChatLines.Clear(); // журнал прошлой комнаты не тащим
            ChatVersion++;
            Log.Info($"SnLobby: хостинг комнаты — '{lobbyName}', слотов {slots}, " +
                     $"{(hasPassword ? "с паролем" : "без пароля")}, {visibility}");
            var options = new HostOptions(SocketType.Steam, GameState.ServerWaiting, MapLoader.Empty)
            {
                MaxConnections = Mathf.Max(1, slots - 1),
                Password = hasPassword ? password : null,
            };
            await NetworkManagerNuclearOption.i.StartHostAsync(options);
            LobbyEvents.RaiseHostResult(Hosting, Hosting ? null
                : Loc.Tr("Сервер не поднялся — подробности в логе"));
            if (Hosting)
                Broadcast();
        }

        private static string LobbyDisplayName(string lobbyName)
        {
            string n = string.IsNullOrWhiteSpace(lobbyName)
                ? $"{SteamFriends.GetPersonaName()} — Supreme Nuclear"
                : lobbyName.Trim();
            return n;
        }

        private static SteamLobby EnsureSteamLobby()
        {
            if (SteamLobby.instance != null)
                return SteamLobby.instance;
            if (!SteamManager.ClientInitialized)
                return null;
            var go = new GameObject("SN_SteamLobby"); // 3d-go
            SteamLobby lobby = go.AddComponent<SteamLobby>();
            lobby.Manager = NetworkManagerNuclearOption.i;
            Log.Info("SnLobby: SteamLobby инстанцирован модом (в меню его не было)");
            return SteamLobby.instance;
        }

        // ---------- участники ----------

        private static void OnPlayerAuthenticated(INetworkPlayer player)
        {
            if (!Hosting)
                return;
            NetworkManagerNuclearOption.i.SendLoadWaitingMessage();
            SendStateTo(player);
        }

        private static void OnPlayerDisconnected(INetworkPlayer player)
        {
            if (!Hosting || player == null || State == null)
                return;
            int id = PlayerIndexOf(player);
            if (id < 0)
                return;
            ReadyIds.Remove(id);
            // Слот игрока освобождается (контроллер → open), армия остаётся.
            foreach (LobbyState.Army a in State.armies)
                if (a.controller == LobbyState.CtrlHuman && a.humanId == id)
                {
                    a.controller = LobbyState.CtrlOpen;
                    a.humanId = -1;
                    Log.Info("SnLobby: слот армии " + a.id + " освобождён (игрок вышел)");
                }
            Broadcast();
        }

        private static int PlayerIndexOf(INetworkPlayer player)
        {
            try
            {
                return player.GetAuthData().SaveData.PlayerIndex;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        private static int HostIndex()
        {
            var local = NetworkManagerNuclearOption.i.Server.LocalPlayer;
            return local != null ? PlayerIndexOf(local) : -1;
        }

        // ---------- команды армий (союз/враг в бою) ----------

        /// <summary>Союз ли фракции-имена с точки зрения плана лобби
        /// (null-имена — не союзники; одинаковые — «свой»). Употребляют
        /// WeaponUseGatePatch (запрет огня по своим) и AiCommanderService.</summary>
        internal static bool AlliedFactions(string factionA, string factionB)
        {
            if (string.IsNullOrEmpty(factionA) || string.IsNullOrEmpty(factionB))
                return false;
            if (factionA == factionB)
                return true;
            if (State?.armies == null)
                return false;
            LobbyState.Army a = null, b = null;
            foreach (LobbyState.Army army in State.armies)
            {
                string name = FactionFactory.ArmyName(army.id);
                if (name == factionA) a = army;
                if (name == factionB) b = army;
            }
            return a != null && b != null && a.controller != LobbyState.CtrlClosed &&
                   b.controller != LobbyState.CtrlClosed && a.team == b.team;
        }

        // ---------- применение плана (патч №16) ----------

        /// <summary>Фракция по плану при спавне персонажа.</summary>
        internal static void ApplyFactionFor(INetworkPlayer player)
        {
            if (!Hosting || State == null || player == null)
                return;
            int id = PlayerIndexOf(player);
            if (id < 0)
                return;
            LobbyState.Army army = LobbyState.ArmyOf(State, id);
            if (army == null || !TryGetHqOfArmy(army, out FactionHQ hq))
                return;
            if (player.TryGetPlayer<Player>(out Player p) && p.HQ == null)
            {
                p.SetFaction(hq, skipPreventJoin: true);
                Log.Info($"SnLobby: участник {id} ({p.GetDisplayName(PlayerNameContext.Other)}) → {army.name}");
            }
        }

        /// <summary>Штаб армии по плану (фракция = рантайм-имя армии).</summary>
        internal static bool TryGetHqOfArmy(LobbyState.Army army, out FactionHQ hq)
        {
            hq = null;
            string factionName = FactionFactory.ArmyName(army.id);
            return FactionRegistry.factionLookup.TryGetValue(factionName, out Faction faction) &&
                   FactionRegistry.HQLookup.TryGetValue(faction, out hq) && hq != null;
        }

        // ---------- запросы гостей ----------

        internal static void RequestTakeSlot(int armyId)
        {
            SendRequest(new LobbyRequest { req = LobbyRequest.TakeSlot, armyId = armyId });
        }

        internal static void RequestLeaveSlot()
        {
            SendRequest(new LobbyRequest { req = LobbyRequest.LeaveSlot });
        }

        internal static void ToggleReady()
        {
            LobbyState.Member me = MyMember();
            SendRequest(new LobbyRequest { req = LobbyRequest.Ready, on = me == null || !me.ready });
        }

        private static LobbyState.Member MyMember()
        {
            if (State == null)
                return null;
            int me = LocalPlayerIndex();
            foreach (LobbyState.Member m in State.members)
                if (m.id == me)
                    return m;
            return null;
        }

        internal static int LocalPlayerIndex()
        {
            if (_localIndex >= 0)
                return _localIndex;
            var local = NetworkManagerNuclearOption.i.Client.Player;
            if (local == null)
                return -1;
            _localIndex = PlayerIndexOf(local);
            return _localIndex;
        }

        private static int _localIndex = -1;

        private static void SendRequest(LobbyRequest request)
        {
            var nm = NetworkManagerNuclearOption.i;
            if (nm?.Client == null || !nm.Client.Active)
                return;
            nm.Client.Send(new SnLobbyMessage { Json = request.ToJson() });
        }

        // ---------- чат комнаты (§12-64е) ----------

        /// <summary>Локальный журнал строк чата (живёт у каждого клиента;
        /// в состояние лобби НЕ входит — Broadcast его не таскает).</summary>
        internal static readonly List<string> ChatLines = new List<string>();
        /// <summary>Растёт на каждую новую строку — панель сравнением видит свежак.</summary>
        internal static int ChatVersion;
        private const int ChatCap = 80;

        /// <summary>Отправка строки: хост — себе + всем (от себя); гость — запрос
        /// хосту, раздачу ведёт хост (имя автора проставляет хост по auth).</summary>
        internal static void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            var nm = NetworkManagerNuclearOption.i;
            if (nm == null)
                return;
            string clean = text.Trim().SanitizeRichText(200);
            if (Hosting)
            {
                string from = LocalName();
                AppendChat(from, clean);
                if (nm.Server != null && nm.Server.Active)
                    nm.Server.SendToAll(
                        new SnLobbyMessage { Json = new LobbyRequest
                            { req = LobbyRequest.Chat, from = from, text = clean }.ToJson() },
                        authenticatedOnly: true, excludeLocalPlayer: true);
            }
            else
                SendRequest(new LobbyRequest { req = LobbyRequest.Chat, text = clean });
        }

        private static void AppendChat(string from, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            ChatLines.Add((string.IsNullOrWhiteSpace(from) ? "?" : from) + ": " + text);
            if (ChatLines.Count > ChatCap)
                ChatLines.RemoveRange(0, ChatLines.Count - ChatCap);
            ChatVersion++;
        }

        /// <summary>Ник локального игрока (по member-списку состояния).</summary>
        private static string LocalName()
        {
            int id = LocalPlayerIndex();
            if (State != null)
                foreach (LobbyState.Member m in State.members)
                    if (m.id == id)
                        return m.name;
            return id == HostIndex() ? Loc.Tr("хост") : "Player " + id;
        }

        /// <summary>Живые люди в слотах, не нажавшие «Готов» (предупреждение
        /// хосту перед СТАРТОМ; канон SupCom — СТАРТ не блокируется, §12-64д).</summary>
        internal static string NotReadyNames()
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return null;
            var names = new List<string>();
            foreach (LobbyState.Army a in State.armies)
                if (a.controller == LobbyState.CtrlHuman && a.humanId >= 0 &&
                    a.humanId != HostIndex() && !ReadyIds.Contains(a.humanId))
                    names.Add(HumanNameById(a.humanId));
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        private static string HumanNameById(int id)
        {
            if (State != null)
                foreach (LobbyState.Member m in State.members)
                    if (m.id == id)
                        return m.name;
            return "Player " + id;
        }

        /// <summary>Пересадка человека хостом (drag-assign, §12-64а): в открытый
        /// слот — сесть, на ИИ — высадить ИИ в освободившийся, к человеку —
        /// обмен слотами. Закрытый слот — no-op.</summary>
        internal static void MovePlayer(int fromArmyId, int toArmyId)
        {
            if (!Hosting || State == null || fromArmyId == toArmyId ||
                State.status != LobbyState.StatusWaiting)
                return;
            LobbyState.Army from = State.armies.Find(a => a.id == fromArmyId);
            LobbyState.Army to = State.armies.Find(a => a.id == toArmyId);
            if (from == null || to == null || to.controller == LobbyState.CtrlClosed ||
                from.controller != LobbyState.CtrlHuman)
                return;
            int mover = from.humanId;
            if (to.controller == LobbyState.CtrlOpen)
            {
                to.controller = LobbyState.CtrlHuman;
                to.humanId = mover;
                from.controller = LobbyState.CtrlOpen;
                from.humanId = -1;
            }
            else if (to.controller == LobbyState.CtrlAi)
            {
                to.controller = LobbyState.CtrlHuman;
                to.humanId = mover;
                from.controller = LobbyState.CtrlAi;
                from.humanId = -1;
            }
            else
            {
                int other = to.humanId;
                to.humanId = mover;
                from.humanId = other; // человек ↔ человек: обмен слотами
            }
            Log.Info($"SnLobby: пересадка хостом — Player {mover}: слот {fromArmyId} → {toArmyId}");
            Broadcast();
        }

        /// <summary>Хендлер гостя: снапшот состояния; карта/армии изменились —
        /// перегенерация локально (превью + мгновенная загрузка; HQ — scene-
        /// объекты карты, набор обязан совпасть с хостом). JSON чат-рассылки
        /// (req=chat) — отдельный путь: журнал локально, состояние не трогаем.</summary>
        internal static void OnClientLobbyMessage(SnLobbyMessage message)
        {
            if (!string.IsNullOrEmpty(message.Json) &&
                message.Json.Contains("\"req\":\"" + LobbyRequest.Chat + "\""))
            {
                LobbyRequest chat = LobbyRequest.FromJson(message.Json);
                if (chat != null && chat.req == LobbyRequest.Chat)
                {
                    AppendChat(chat.from, chat.text);
                    return;
                }
            }
            LobbyState state = LobbyState.FromJson(message.Json);
            if (state == null)
                return;
            IsGuest = !Hosting;
            bool mapChanged = State?.map == null || state.map == null ||
                State.map.template != state.map.template || State.map.seed != state.map.seed ||
                State.map.players != state.map.players;
            bool armiesChanged = State == null || ArmiesDiffer(State.armies, state.armies);
            State = state;
            StateChanged?.Invoke();
            if (!Hosting && state.map != null && (mapChanged || armiesChanged) &&
                State.status == LobbyState.StatusWaiting)
            {
                MapGenAsync.Request(state.map.template, state.map.seed, state.map.players,
                    buildMission: false, supersede: true,
                    armies: state.armies, victory: state.victory);
            }
        }

        private static bool ArmiesDiffer(List<LobbyState.Army> a, List<LobbyState.Army> b)
        {
            if (a == null || b == null || a.Count != b.Count)
                return true;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].controller != b[i].controller || a[i].team != b[i].team ||
                    a[i].pos != b[i].pos || a[i].humanId != b[i].humanId)
                    return true;
            }
            return false;
        }

        /// <summary>Хендлер хоста: запрос гостя (слот/готовность).</summary>
        internal static void OnServerLobbyRequest(INetworkPlayer player, SnLobbyMessage message)
        {
            LobbyRequest req = LobbyRequest.FromJson(message.Json);
            if (req == null || !Hosting || _launching ||
                State == null || State.status != LobbyState.StatusWaiting)
                return;
            int id = PlayerIndexOf(player);
            if (id < 0 || id == HostIndex())
                return;
            switch (req.req)
            {
                case LobbyRequest.TakeSlot:
                {
                    LobbyState.Army target = State.armies.Find(a => a.id == req.armyId);
                    if (target == null || target.controller != LobbyState.CtrlOpen)
                        break;
                    FreeSlotOf(id);
                    target.controller = LobbyState.CtrlHuman;
                    target.humanId = id;
                    Log.Info($"SnLobby: участник {id} → слот армии {target.id}");
                    break;
                }
                case LobbyRequest.LeaveSlot:
                    FreeSlotOf(id);
                    break;
                case LobbyRequest.Ready:
                    if (req.on)
                        ReadyIds.Add(id);
                    else
                        ReadyIds.Remove(id);
                    break;
                case LobbyRequest.Chat:
                {
                    string text = (req.text ?? "").Trim();
                    if (text.Length == 0)
                        return; // пустую строку не ретранслируем и не журналируем
                    string from = null;
                    try { from = player.GetAuthData().ClientReportedSteamName; }
                    catch (System.Exception) { }
                    from = string.IsNullOrWhiteSpace(from) ? "Player " + id : from.SanitizeRichText(64);
                    AppendChat(from, text.SanitizeRichText(200));
                    // Ретрансляция ВСЕМ включая автора: гость слал запрос — его
                    // локального журнала эта строка ещё не касается.
                    NetworkManagerNuclearOption.i.Server.SendToAll(
                        new SnLobbyMessage { Json = new LobbyRequest
                            { req = LobbyRequest.Chat, from = from, text = text }.ToJson() },
                        authenticatedOnly: true, excludeLocalPlayer: false);
                    return; // чат не в состоянии — Broadcast не нужен
                }
            }
            Broadcast();
        }

        private static void FreeSlotOf(int playerId)
        {
            foreach (LobbyState.Army a in State.armies)
                if (a.controller == LobbyState.CtrlHuman && a.humanId == playerId)
                {
                    a.controller = LobbyState.CtrlOpen;
                    a.humanId = -1;
                }
        }

        // ---------- хост: правки лобби ----------

        /// <summary>Хост переключает готовность сидящего в слоте (стол, клик по
        /// столбцу «Готов») — синхронизация перед стартом.</summary>
        internal static void ToggleSlotReady(int armyId)
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null || a.controller != LobbyState.CtrlHuman || a.humanId < 0)
                return;
            if (ReadyIds.Contains(a.humanId))
                ReadyIds.Remove(a.humanId);
            else
                ReadyIds.Add(a.humanId);
            Broadcast();
        }

        /// <summary>Хост ставит слот в ИИ с КОНКРЕТНЫМ профилем (FAF-дропдаун
        /// «AI: Easy/Normal/…», плейтест 2026-11-07): aiType → AI/&lt;type&gt;.json,
        /// §12-49. В миссию aiType не запекается — регенерация карты не нужна.</summary>
        internal static void SetAiProfile(int armyId, string type)
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null)
                return;
            if (System.Array.IndexOf(AiProfile.Types, type) < 0)
                type = "commander";
            a.controller = LobbyState.CtrlAi;
            a.aiType = type;
            Log.Info($"SnLobby: слот {armyId} → ИИ ({type})");
            Broadcast();
        }

        /// <summary>Хост занимает слот сам (столбец «Занять», плейтест
        /// 2026-11-07: «логика занятия слотов не работает» — хост-ветка
        /// перезаписывалась готовностью мимо открытого слота): открытый —
        /// сесть, ИИ — сесть вместо ИИ. Прежний слот хоста → Открыт.</summary>
        internal static void SeatHostAt(int armyId)
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            LobbyState.Army target = State.armies.Find(a => a.id == armyId);
            if (target == null ||
                (target.controller != LobbyState.CtrlOpen && target.controller != LobbyState.CtrlAi))
                return;
            int hostId = HostIndex();
            foreach (LobbyState.Army a in State.armies)
                if (a.controller == LobbyState.CtrlHuman && a.humanId == hostId && a.id != armyId)
                {
                    a.controller = LobbyState.CtrlOpen; // один человек — один слот
                    a.humanId = -1;
                }
            target.controller = LobbyState.CtrlHuman;
            target.humanId = hostId;
            Log.Info($"SnLobby: хост пересел в слот {armyId}");
            Broadcast();
        }

        /// <summary>Хост не усажен в слот (Broadcast шёл до auth LocalPlayer)? —
        /// пересадка и пересылка. Чинит «?» вместо ника хоста в столе.</summary>
        internal static void EnsureHostSeated()
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            int hostId = HostIndex();
            if (hostId < 0)
                return;
            foreach (LobbyState.Army a in State.armies)
                if (a.controller == LobbyState.CtrlHuman && a.humanId == hostId)
                    return; // уже усажен
            Broadcast(); // пересоберёт members и усадит хоста в слот 1
        }

        /// <summary>Прямая установка контроллера слота (контекстное меню никнейма,
        /// как у FAF: Close/Occupy/AI). human — только через AssignHuman.</summary>
        internal static void SetArmyController(int armyId, string controller)
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null || a.controller == controller)
                return;
            if (controller != LobbyState.CtrlClosed && controller != LobbyState.CtrlOpen &&
                controller != LobbyState.CtrlAi)
                return;
            a.controller = controller;
            if (controller != LobbyState.CtrlHuman)
                a.humanId = -1;
            Broadcast();
        }

        /// <summary>Цикл контроллера слота: closed → open → ai → open…
        /// (human освобождает слот в open).</summary>
        internal static void CycleController(int armyId)
        {
            if (!Hosting || State == null)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null)
                return;
            if (a.controller == LobbyState.CtrlClosed) a.controller = LobbyState.CtrlOpen;
            else if (a.controller == LobbyState.CtrlOpen) a.controller = LobbyState.CtrlAi;
            else if (a.controller == LobbyState.CtrlAi) a.controller = LobbyState.CtrlOpen;
            else { a.controller = LobbyState.CtrlOpen; a.humanId = -1; }
            Broadcast();
        }

        /// <summary>Цикл цвета армии (палитра FactionFactory, 4 цвета).</summary>
        internal static void CycleColor(int armyId)
        {
            if (!Hosting || State == null)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null)
                return;
            a.colorIdx = (a.colorIdx + 1) % 4;
            Broadcast();
        }

        internal static void CycleTeam(int armyId)
        {
            if (!Hosting || State == null)
                return;
            LobbyState.Army a = State.armies.Find(x => x.id == armyId);
            if (a == null)
                return;
            a.team = a.team % 4 + 1; // союзы 1..4
            Broadcast();
        }

        internal static void AssignHuman(int armyId, int playerId)
        {
            if (!Hosting || State == null)
                return;
            LobbyState.Army target = State.armies.Find(a => a.id == armyId);
            if (target == null)
                return;
            FreeSlotOf(playerId);
            target.controller = LobbyState.CtrlHuman;
            target.humanId = playerId;
            Broadcast();
        }

        internal static void CycleVictory()
        {
            if (!Hosting || State == null)
                return;
            State.victory = State.victory == LobbyState.VictoryCapture ? LobbyState.VictoryAnnihilate
                : State.victory == LobbyState.VictoryAnnihilate ? LobbyState.VictoryEndless
                : LobbyState.VictoryCapture;
            Broadcast();
        }

        /// <summary>Хост крутит «Стороны» — слоты перестраиваются СРАЗУ (до
        /// генерации карты; юзер 2026-11-06: пустые слоты должны быть доступны).
        /// Занятые слоты переносятся; выпадающие из пула освобождаются.</summary>
        internal static void SetPool(int pool)
        {
            if (!Hosting || State == null || State.status != LobbyState.StatusWaiting)
                return;
            pool = Mathf.Clamp(pool, 2, LobbyState.MaxArmies);
            if (pool == State.pool)
                return;
            State.pool = pool;
            var oldByPos = new Dictionary<int, LobbyState.Army>();
            foreach (LobbyState.Army a in State.armies)
                oldByPos[a.pos] = a;
            var fresh = LobbyState.ArmiesForPool(pool);
            foreach (LobbyState.Army a in fresh)
            {
                if (!oldByPos.TryGetValue(a.pos, out LobbyState.Army prev))
                    continue;
                a.team = prev.team;
                a.colorIdx = prev.colorIdx;
                if (prev.controller == LobbyState.CtrlHuman && prev.humanId >= 0)
                {
                    a.controller = LobbyState.CtrlHuman;
                    a.humanId = prev.humanId;
                }
                else if (prev.controller == LobbyState.CtrlAi)
                    a.controller = LobbyState.CtrlAi;
            }
            State.armies = fresh;
            Broadcast();
        }

        /// <summary>Карта сгенерирована хостом в лобби (MapSetupBlock/панель).
        /// Пул карты обязан совпасть с выбранным столом (SetPool) — иначе гейт
        /// LaunchBlockReason потребует перегенерации.</summary>
        internal static void SetMap(string template, int seed, int players, string saveName)
        {
            if (!Hosting)
                return;
            State.map = new LobbyState.MapInfo
            {
                template = template,
                seed = seed,
                players = players,
                saveName = saveName,
            };
            Broadcast();
        }

        // ---------- синк ----------

        private static void Broadcast()
        {
            if (!Hosting || State == null)
                return;
            int hostId = HostIndex();
            State.hostId = hostId;
            State.members.Clear();
            var players = NetworkManagerNuclearOption.i.Server.AuthenticatedPlayers;
            if (players != null)
                foreach (INetworkPlayer player in players)
                {
                    int id = PlayerIndexOf(player);
                    if (id < 0)
                        continue;
                    string name = null;
                    try { name = player.GetAuthData().ClientReportedSteamName; }
                    catch (System.Exception) { }
                    if (string.IsNullOrWhiteSpace(name))
                        name = "Player " + id;
                    State.members.Add(new LobbyState.Member
                    {
                        id = id,
                        name = name.SanitizeRichText(64),
                        ready = ReadyIds.Contains(id),
                    });
                }
            // Хост — командир армии 1 (его слот по умолчанию).
            if (hostId >= 0)
            {
                bool hostSeated = false;
                foreach (LobbyState.Army a in State.armies)
                    if (a.controller == LobbyState.CtrlHuman && a.humanId == hostId)
                        hostSeated = true;
                if (!hostSeated)
                {
                    LobbyState.Army hostArmy = State.armies.Find(a => a.id == 0);
                    // Плейтест 2026-11-06: слот 0 с humanId=-1 (дефолт) блокировал
                    // посадку хоста — гейт был «controller != Human», а слот-то Human,
                    // только пустой. Сажаем, если в слоте 0 не сам хост.
                    if (hostArmy != null && hostArmy.humanId != hostId)
                    {
                        hostArmy.controller = LobbyState.CtrlHuman;
                        hostArmy.humanId = hostId;
                    }
                }
            }
            string json = State.ToJson();
            if (_lobby != null && _lobby.Value.IsValid)
            {
                _lobby.Value.SetData(LobbyState.DataKey, json);
                if (State.map != null)
                    _lobby.Value.SetData("map_name", ("SN " + State.map.template + " " +
                        State.map.seed.ToString("X8")).SanitizeRichText(128));
            }
            NetworkManagerNuclearOption.i.Server.SendToAll(
                new SnLobbyMessage { Json = json }, authenticatedOnly: true, excludeLocalPlayer: true);
            StateChanged?.Invoke();
        }

        private static void SendStateTo(INetworkPlayer player)
        {
            if (State == null)
                return;
            player.Send(new SnLobbyMessage { Json = State.ToJson() });
        }

        // ---------- СТАРТ (рецепт LoadNext) ----------

        /// <summary>Гейт СТАРТА: карта Ready и план армий валиден.</summary>
        internal static bool ReadyToLaunch()
        {
            return LaunchBlockReason() == null;
        }

        /// <summary>Причина неготовности СТАРТА (null — готов; текст — в панель).</summary>
        internal static string LaunchBlockReason()
        {
            if (!Hosting || State == null)
                return Loc.Tr("Лобби не создано");
            if (State.map == null)
                return Loc.Tr("Карта не выбрана — сгенерируй её в блоке «Карта»");
            if (State.pool != State.map.players)
                return Loc.F("Пул стола ({0}) ≠ пулу карты ({1}) — выбери стороны заново",
                    State.pool, State.map.players);
            if (MapGenAsync.Current != MapGenAsync.Phase.Done ||
                MapGenAsync.TemplateName != State.map.template ||
                MapGenAsync.Seed != State.map.seed ||
                MapGenAsync.Players != State.map.players)
                return Loc.Tr("Карта с текущими армиями ещё не сгенерирована");
            string invalid = LobbyState.ValidateForLaunch(State);
            if (invalid != null)
                return Loc.F("План армий некорректен: {0}", invalid);
            // СТАРТ ждёт «Готов» от всех сидящих людей (решение юзера 2026-11-07 —
            // смена прежнего канона «хост вольен», §12-64д): флаг готовности
            // реальный гейт, хост своего «Готова» не требует.
            string notReady = NotReadyNames();
            if (notReady != null)
                return Loc.F("Не готовы: {0} — пусть нажмут «Готов»", notReady);
            return null;
        }

        /// <summary>СТАРТ: карта у всех → фракции по плану (№16) → нативный каскад.</summary>
        internal static async UniTask Launch()
        {
            if (!ReadyToLaunch())
                return;
            _launching = true;
            HoldStart = true;
            State.status = LobbyState.StatusStarting;
            Broadcast();
            try
            {
                var key = new MissionKey(State.map.saveName, MissionGroup.User);
                if (!key.TryLoad(out _mission, out string loadError))
                {
                    Log.Error("SnLobby: миссия не загрузилась — " + loadError);
                    AbortLaunch();
                    return;
                }
                var nm = NetworkManagerNuclearOption.i;
                nm.SendServerLoadingMessage(_mission.Name);
                DestroyHostEmptyCharacter();
                MissionManager.SetMission(_mission, checkIfSame: false);
                GameManager.SetGameState(GameState.Multiplayer);
                GameManager.SetupGame();
                var progress = new Progress<float>(value =>
                    nm.SendServerLoadingMessage($"{_mission.Name}\nLoading Scene: {(int)(value * 100f)}%"));
                (bool ok, MapLoader.LoadResult result) = await nm.ServerLoadMapScene(_mission.MapKey, progress);
                if (!ok)
                {
                    Log.Error("SnLobby: загрузка карты не удалась — " + result);
                    AbortLaunch();
                    return;
                }
                // HQ заспавнены патчем №12; асы после старта — нативное меню фракций,
                // поэтому все армии закрыты от свободного входа (состав — план слотов).
                foreach (LobbyState.Army a in State.armies)
                    if (a.controller != LobbyState.CtrlClosed && TryGetHqOfArmy(a, out FactionHQ hq))
                        hq.NetworkpreventJoin = true;
                nm.Host_SceneLoaded();       // персонаж хоста (№16 применит фракцию)
                nm.SendLoadMapMessageAll();  // клиенты грузят карту → спавн → №16
                HoldStart = false;
                MissionManager.StartMission();
                CommanderModeController.PendingAutoActivateMission = _mission.Name;
                State.status = LobbyState.StatusRunning;
                Broadcast();
                Log.Info("SnLobby: миссия запущена — " + _mission.Name +
                         " (армий " + LobbyState.LiveArmies(State.armies) +
                         ", победа: " + LobbyState.EffectiveVictory(State) + ")");
            }
            finally
            {
                _launching = false;
            }
        }

        private static void AbortLaunch()
        {
            HoldStart = false;
            State.status = LobbyState.StatusWaiting;
            Broadcast();
        }

        private static void DestroyHostEmptyCharacter()
        {
            var nm = NetworkManagerNuclearOption.i;
            var local = nm.Server.LocalPlayer;
            if (local == null || !local.HasCharacter)
                return;
            if (local.TryGetPlayer<Player>(out Player p) && p != null)
            {
                local.RemoveAllOwnedObject(sendAuthorityChangeEvent: false);
                nm.ServerObjectManager.Destroy(p.Identity);
                Log.Info("SnLobby: EMPTY-персонаж хоста снят (чистый спавн на карте)");
            }
        }

        /// <summary>«Распустить»/«Покинуть» — нативный Stop.</summary>
        internal static void Leave()
        {
            Log.Info(Hosting ? "SnLobby: роспуск лобби (Stop сервера)"
                             : "SnLobby: выход из лобби (Stop клиента)");
            ChatLines.Clear(); // журнал комнаты умер вместе с ней
            ChatVersion++;
            NetworkManagerNuclearOption.i.Stop(setDisconnectReason: false);
        }
    }

    /// <summary>События жизненного цикла лобби для UI.</summary>
    internal static class LobbyEvents
    {
        /// <summary>Хост поднялся (ok=true, error=null) или создание сорвалось
        /// (ok=false, error — текст для панели). Снаружи — только Raise*/подписка.</summary>
        internal static event System.Action<bool, string> HostResult;

        internal static void RaiseHostResult(bool ok, string error) =>
            HostResult?.Invoke(ok, error);
    }
}

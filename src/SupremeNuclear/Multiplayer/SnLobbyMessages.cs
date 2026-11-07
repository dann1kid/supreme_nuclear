using System;
using Mirage;
using Mirage.Serialization;
using NuclearOption.Networking;

namespace SupremeNuclear
{
    /// <summary>Кастомное Mirage-сообщение лобби (один тип, JSON в поле):
    /// хост → все — снапшот LobbyState; гость → хост — LobbyRequest.
    /// Мод не weaved — вивер для нас недоступен, поэтому сериализацию и id
    /// регистрируем в рантайме ровно тем же способом, каким вивер игры
    /// регистрирует свои сообщения (GeneratedNetworkCode: Writer<T>.Write,
    /// Reader<T>.Read, MessagePacker.RegisterMessage<T> — публичные API).</summary>
    public struct SnLobbyMessage
    {
        public string Json;
    }

    /// <summary>Регистрация сообщения и хендлеров. ИДЕМПОТЕНТНО и ОТЛОЖЕННО:
    /// NetworkManagerNuclearOption создаётся НЕ при загрузке плагина (плейтест
    /// 2026-11-06: `i` == null в Awake плагина — менеджер грузится префабом
    /// позже), поэтому Initialize зовут повторно (MapGenAsync.Update →
    /// SnLobbyService.EnsureInitialized), пока менеджер не появится. Хендлеры —
    /// на Client/Server.Started (как у NMNO.Awake), чтобы MessageHandler
    /// гарантированно существовал.</summary>
    internal static class SnLobbyMessages
    {
        private static bool _registered;

        /// <summary>false — менеджера ещё нет, повторить позже.</summary>
        public static bool TryInitialize()
        {
            if (_registered)
                return true;
            var nm = NetworkManagerNuclearOption.i;
            if (nm == null)
                return false;
            // Писатель/читатель: JSON одной строкой (State/Request различает
            // получатель по направлению и содержимому — поля req/статус).
            Writer<SnLobbyMessage>.Write = (writer, value) => writer.WriteString(value.Json ?? "");
            Reader<SnLobbyMessage>.Read = reader => new SnLobbyMessage
            {
                Json = reader.ReadString(),
            };
            MessagePacker.RegisterMessage<SnLobbyMessage>();
            nm.Client.Started.AddListener(RegisterClientHandler);
            nm.Server.Started.AddListener(RegisterServerHandler);
            _registered = true;
            Log.Info("SnLobbyMessages: сообщение зарегистрировано (Writer/Reader/MessagePacker)");
            return true;
        }

        private static void RegisterClientHandler()
        {
            NetworkManagerNuclearOption.i.Client.MessageHandler
                .RegisterHandler<SnLobbyMessage>(SnLobbyService.OnClientLobbyMessage);
        }

        private static void RegisterServerHandler()
        {
            NetworkManagerNuclearOption.i.Server.MessageHandler
                .RegisterHandler<SnLobbyMessage>(SnLobbyService.OnServerLobbyRequest);
        }
    }
}

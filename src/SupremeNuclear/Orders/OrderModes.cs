namespace SupremeNuclear
{
    public enum OrderMode
    {
        None,
        Move,
        AttackMove,
        Patrol,
        Attack,
        /// <summary>Залп: ЛКМ по цели/земле — подлёт и одновременный пуск выбранных
        /// ракетных станций по нескольким целям (Shift+ЛКМ — доб. цели; SalvoService).</summary>
        Salvo,
        /// <summary>Распределение (бывш. «атака веером»): ЛКМ по врагам накапливает
        /// список целей, ЛКМ по земле — раздать группу по целям (ближайшая цель
        /// с балансом N/M, MoveOrderService).</summary>
        DisperseAttack,
        /// <summary>ЯО (v0.139, §2.14-3): ЛКМ по земле — точка пуска ядерной ракеты
        /// для выделенных носителей (корабли: очередь точек, ракета по готовности
        /// заряда; я-авиация: одноразовое разрешение + атака-движение в точку);
        /// Shift — серия точек. Кольцо у курсора = зона поражения.</summary>
        Nuke,
        /// <summary>Охрана (v0.158, канон SupCom guard, §2.11): ЛКМ по союзному
        /// юниту/зданию — охрана. Follow-модель эскорта; инженер авто-чинит
        /// охраняемого, боевое прикрытие перехватывает угрозы у цели (Guard ≠
        /// Escort). Авиации не назначается (у бортов escort-сегменты).</summary>
        Guard,
        Cancel
    }

    /// <summary>Режим выдачи приказа (SupCom-стиль, v0.114 — код приведён к контракту):
    /// команда, заряженная хоткеем/панелью, применяется ЛКМ по точке/цели и ОСТАЁТСЯ
    /// заряженной (можно накликать серию); ПКМ гасит заряд без выдачи; Esc — тоже.
    /// Дефолт (заряда нет) — контекстные приказы по ПКМ (движение/атака/эскорт/
    /// посадка/ралли/достройка). Выдача НЕ сбрасывает заряд — только ПКМ/Esc/пустое
    /// выделение (MoveOrderService.Tick).</summary>
    public static class OrderModes
    {
        public static OrderMode Pending = OrderMode.None;

        public static string Label(OrderMode mode) =>
            mode switch
            {
                OrderMode.Move => Loc.Tr("Движение"),
                OrderMode.AttackMove => Loc.Tr("Атака-движение"),
                OrderMode.Patrol => Loc.Tr("Патруль"),
                OrderMode.Attack => Loc.Tr("Атака"),
                OrderMode.Salvo => Loc.Tr("Залп"),
                OrderMode.DisperseAttack => Loc.Tr("Распределение"),
                OrderMode.Nuke => Loc.Tr("Ядерный удар"),
                OrderMode.Guard => Loc.Tr("Охрана"),
                OrderMode.Cancel => Loc.Tr("Стоп"),
                _ => null
            };
    }
}

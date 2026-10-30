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
                OrderMode.Cancel => Loc.Tr("Стоп"),
                _ => null
            };
    }
}

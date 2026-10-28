namespace SupremeNuclear
{
    public enum OrderMode
    {
        None,
        Move,
        AttackMove,
        Patrol,
        Attack,
        /// <summary>Залп: ПКМ по цели/земле — подлёт и одновременный пуск выбранных
        /// ракетных станций по нескольким целям (SalvoService).</summary>
        Salvo,
        /// <summary>Атака веером: ПКМ по врагам накапливает список целей, ПКМ по земле —
        /// раздать группу по целям (ближайшая цель с балансом N/M, MoveOrderService).</summary>
        DisperseAttack,
        Cancel
    }

    /// <summary>Режим выдачи приказа (SupCom-стиль): выбранная команда применяется
    /// следующим ЛКМ по точке/цели; ПКМ отменяет режим.</summary>
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
                OrderMode.DisperseAttack => Loc.Tr("Атака веером"),
                OrderMode.Cancel => Loc.Tr("Стоп"),
                _ => null
            };
    }
}

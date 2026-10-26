namespace SupremeNuclear
{
    public enum OrderMode
    {
        None,
        Move,
        AttackMove,
        Patrol,
        Attack,
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
                OrderMode.Move => "Движение",
                OrderMode.AttackMove => "Атака-движение",
                OrderMode.Patrol => "Патруль",
                OrderMode.Attack => "Атака",
                OrderMode.Cancel => "Стоп",
                _ => null
            };
    }
}

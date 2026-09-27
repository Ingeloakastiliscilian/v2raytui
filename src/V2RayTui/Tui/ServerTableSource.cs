using Terminal.Gui.Views;

namespace V2RayTui.Tui;

/// <summary>Table model over the current server rows. Values are read live, so test results show up on redraw.</summary>
internal sealed class ServerTableSource(IReadOnlyList<ServerRow> rows) : ITableSource
{
    public const int ColMark = 0;
    public const int ColIp = 1;
    public const int ColName = 2;
    public const int ColType = 3;
    public const int ColAddress = 4;
    public const int ColTransport = 5;
    public const int ColSub = 6;
    public const int ColDelay = 7;
    public const int ColSpeed = 8;

    public IReadOnlyList<ServerRow> Rows { get; } = rows;

    public string[] ColumnNames { get; } =
    [
        " ",
        "IP",
        Loc.T("Name", "Имя"),
        Loc.T("Type", "Тип"),
        Loc.T("Address", "Адрес"),
        Loc.T("Transport", "Транспорт"),
        Loc.T("Subscription", "Подписка"),
        Loc.T("Delay", "Задержка"),
        Loc.T("Speed, MB/s", "Скорость, МБ/с"),
    ];

    public int Columns => ColumnNames.Length;

    int ITableSource.Rows => Rows.Count;

    /// <summary>Largest speed in the list — the scale of the speed bars.</summary>
    public decimal MaxSpeed => Rows.Count == 0 ? 0 : Rows.Max(r => r.Speed);

    public object this[int row, int col]
    {
        get
        {
            if (row < 0 || row >= Rows.Count)
            {
                return "";
            }
            var r = Rows[row];
            return col switch
            {
                ColMark => r switch { { IsActive: true, Marked: true } => "◆", { IsActive: true } => "▶", { Marked: true } => "✓", _ => " " },
                // Only the flag of the exit country; the full IP is in the details (i).
                ColIp => GeoIp.Flag(GeoIp.CountryFromIpInfo(r.IpInfo)),
                ColName => r.Remarks,
                ColType => Theme.ProtocolShort(r.ConfigType),
                ColAddress => r.Endpoint,
                ColTransport => r.Transport,
                ColSub => r.SubRemarks,
                ColDelay => r.DelayStatus.IsNotEmpty() ? r.DelayStatus : r.Delay switch
                {
                    > 0 => $"{r.Delay} ms",
                    < 0 => "✗",
                    _ => "",
                },
                ColSpeed => r.SpeedStatus.IsNotEmpty() ? r.SpeedStatus : r.Speed > 0 ? $"{Theme.Bar(r.Speed, MaxSpeed)} {r.Speed,5:0.0}" : "",
                _ => "",
            };
        }
    }
}

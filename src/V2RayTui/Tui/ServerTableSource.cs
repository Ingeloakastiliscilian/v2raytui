using Terminal.Gui.Views;

namespace V2RayTui.Tui;

/// <summary>Table model over the current server rows. Values are read live, so test results show up on redraw.</summary>
internal sealed class ServerTableSource(IReadOnlyList<ServerRow> rows) : ITableSource
{
    public const int ColMark = 0;
    public const int ColType = 1;
    public const int ColName = 2;
    public const int ColAddress = 3;
    public const int ColTransport = 4;
    public const int ColSub = 5;
    public const int ColDelay = 6;
    public const int ColSpeed = 7;
    public const int ColIp = 8;

    public IReadOnlyList<ServerRow> Rows { get; } = rows;

    public string[] ColumnNames { get; } =
    [
        " ",
        Loc.T("Type", "Тип"),
        Loc.T("Name", "Имя"),
        Loc.T("Address", "Адрес"),
        Loc.T("Transport", "Транспорт"),
        Loc.T("Subscription", "Подписка"),
        Loc.T("Delay", "Задержка"),
        Loc.T("Speed", "Скорость"),
        "IP",
    ];

    public int Columns => ColumnNames.Length;

    int ITableSource.Rows => Rows.Count;

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
                ColMark => (r.IsActive ? "▶" : " ") + (r.Marked ? "✓" : " "),
                ColType => r.TypeName,
                ColName => r.Remarks,
                ColAddress => r.Endpoint,
                ColTransport => r.Transport,
                ColSub => r.SubRemarks,
                ColDelay => r.DelayStatus.IsNotEmpty() ? r.DelayStatus : r.Delay switch
                {
                    > 0 => $"{r.Delay} ms",
                    < 0 => "✗",
                    _ => "",
                },
                ColSpeed => r.SpeedStatus.IsNotEmpty() ? r.SpeedStatus : r.Speed > 0 ? $"{r.Speed:0.0} MB/s" : "",
                ColIp => r.IpInfo,
                _ => "",
            };
        }
    }
}

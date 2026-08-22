using System.Globalization;
using System.Windows.Data;

namespace LiteTerm.App;

public sealed class ServerGroupNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? "未分组" : ((string)value).Trim();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LastConnectedAtConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset lastConnectedAt
            ? $"最近连接：{lastConnectedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "尚未连接";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using VidLex.Core.Models;

namespace VidLex.App.Converters
{
    /// <summary>Цвет бейджа статуса: зелёный — успех и активные этапы, янтарный — пропуск/остановка, красный — ошибка.</summary>
    public sealed class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var background = string.Equals(parameter as string, "bg", StringComparison.Ordinal);
            string key;
            switch (value is JobStatus s ? s : JobStatus.Waiting)
            {
                case JobStatus.Completed: key = background ? "AccentSoftBrush" : "AccentBrush"; break;
                case JobStatus.Error: key = background ? "ErrorSoftBrush" : "ErrorBrush"; break;
                case JobStatus.Skipped:
                case JobStatus.Stopped: key = background ? "WarningSoftBrush" : "WarningBrush"; break;
                case JobStatus.Waiting: key = background ? "Surface2Brush" : "TextSecondaryBrush"; break;
                default: key = background ? "AccentSoftBrush" : "AccentHoverBrush"; break;
            }
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class LogLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key;
            switch (value is LogLevel l ? l : LogLevel.Info)
            {
                case LogLevel.Error: key = "ErrorBrush"; break;
                case LogLevel.Warning: key = "WarningBrush"; break;
                case LogLevel.Debug: key = "TextSecondaryBrush"; break;
                default: key = "AccentBrush"; break;
            }
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var b = value is bool v && v;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    /// <summary>Сравнение перечисления с параметром — для RadioButton.</summary>
    public sealed class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Enum.Parse(targetType, parameter.ToString()) : Binding.DoNothing;
    }

    public sealed class PercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is double d ? $"{d:0}%" : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

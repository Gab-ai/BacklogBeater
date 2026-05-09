using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace GameRecommender
{
    public static class IconGlyphs
    {
        public const string Info = "\uE946";
        public const string Play = "\uE768";
        public const string Library = "\uE8F1";
        public const string Reject = "\uE711";
    }

    public static class RecommendationEngine
    {
        public static string FormatTime(long seconds)
        {
            if (seconds <= 0) return "Not played";
            long h = seconds / 3600;
            long m = (seconds % 3600) / 60;
            if (h > 0) return m > 0 ? $"{h}h {m}m" : $"{h}h";
            return m > 0 ? $"{m}m" : "< 1m";
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool> canExecute;

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => canExecute == null || canExecute();
        public void Execute(object parameter) => execute();
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> execute;
        private readonly Func<T, bool> canExecute;

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => canExecute == null || canExecute(parameter is T t ? t : default);
        public void Execute(object parameter) => execute(parameter is T t ? t : default);
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public static readonly BoolToVisibilityConverter Instance = new BoolToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Visible;
    }

    public class IntEqualsToVisibilityConverter : IValueConverter
    {
        public static readonly IntEqualsToVisibilityConverter Instance = new IntEqualsToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => ValuesMatch(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static bool ValuesMatch(object value, object parameter)
            => int.TryParse(value?.ToString(), out var current) &&
               int.TryParse(parameter?.ToString(), out var expected) &&
               current == expected;
    }

    public class IntEqualsToBoolConverter : IValueConverter
    {
        public static readonly IntEqualsToBoolConverter Instance = new IntEqualsToBoolConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => int.TryParse(value?.ToString(), out var current) &&
               int.TryParse(parameter?.ToString(), out var expected) &&
               current == expected;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool selected && selected && int.TryParse(parameter?.ToString(), out var expected))
                return expected;
            return Binding.DoNothing;
        }
    }

    public class InverseBoolConverter : IValueConverter
    {
        public static readonly InverseBoolConverter Instance = new InverseBoolConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => !(value is bool b && b);
    }

    public class AnyTrueToVisibilityConverter : IMultiValueConverter
    {
        public static readonly AnyTrueToVisibilityConverter Instance = new AnyTrueToVisibilityConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => values.Any(v => v is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class AllFalseConverter : IMultiValueConverter
    {
        public static readonly AllFalseConverter Instance = new AllFalseConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => !values.Any(v => v is bool b && b);

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public static readonly CountToVisibilityConverter Instance = new CountToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class ZeroToVisibilityConverter : IValueConverter
    {
        public static readonly ZeroToVisibilityConverter Instance = new ZeroToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class PlaytimeConverter : IValueConverter
    {
        public static readonly PlaytimeConverter Instance = new PlaytimeConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long seconds) return RecommendationEngine.FormatTime(seconds);
            if (value is ulong us) return RecommendationEngine.FormatTime((long)us);
            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public class AllZeroToVisibilityConverter : IMultiValueConverter
    {
        public static readonly AllZeroToVisibilityConverter Instance = new AllZeroToVisibilityConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            foreach (var v in values)
                if (v is int i && i > 0) return Visibility.Collapsed;
            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

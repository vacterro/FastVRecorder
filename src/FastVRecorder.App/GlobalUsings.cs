// The app enables both WPF and WinForms (tray icon), and both implicit usings expose types with
// the same names. These aliases pin the WPF types the UI code actually means.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using UserControl = System.Windows.Controls.UserControl;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Color = System.Windows.Media.Color;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using SolidColorBrush = System.Windows.Media.SolidColorBrush;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using FontFamily = System.Windows.Media.FontFamily;
global using FlowDirection = System.Windows.FlowDirection;

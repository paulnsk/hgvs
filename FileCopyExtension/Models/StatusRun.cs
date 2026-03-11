using System.Windows;
using System.Windows.Media;

namespace FileCopyExtension.Models;

public class StatusRun
{
    public string Text { get; set; }
    public Brush Foreground { get; set; } = Brushes.White; // В XAML настроен Fallback
    public FontWeight FontWeight { get; set; } = FontWeights.Normal;
    public FontStyle FontStyle { get; set; } = FontStyles.Normal;
}
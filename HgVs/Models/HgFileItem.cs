namespace HgVs.Models;

public class HgFileItem : System.ComponentModel.INotifyPropertyChanged
{
    private int _lineNumber;
    private int _fileGroupIndex;
    private bool _isReviewed;

    public string Status { get; set; }
    public string Path { get; set; }
    public bool IsUnchanged { get; set; }
    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            if (_isReviewed == value) return;
            _isReviewed = value;
            OnPropertyChanged();
        }
    }
    public string TodoText { get; set; }
    public string Identity { get; set; }
    public int FileGroupIndex
    {
        get => _fileGroupIndex;
        set
        {
            if (_fileGroupIndex == value) return;
            _fileGroupIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAlternateGroup));
        }
    }
    public bool IsTodoItem => LineNumber > 0;
    public bool IsAlternateGroup => IsTodoItem && FileGroupIndex % 2 == 1;

    public int LineNumber
    {
        get => _lineNumber;
        set
        {
            if (_lineNumber == value) return;
            _lineNumber = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
        }
    }

    public string DisplayPath => IsTodoItem
        ? string.Concat(System.IO.Path.GetFileName(Path), System.IO.Path.VolumeSeparatorChar, LineNumber)
        : Path;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

    public void UpdateFrom(HgFileItem other)
    {
        if (other == null) throw new System.ArgumentNullException(nameof(other));
        Status = other.Status;
        Path = other.Path;
        IsUnchanged = other.IsUnchanged;
        IsReviewed = other.IsReviewed;
        TodoText = other.TodoText;
        FileGroupIndex = other.FileGroupIndex;
        LineNumber = other.LineNumber;
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
